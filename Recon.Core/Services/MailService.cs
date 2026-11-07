using System.Collections.Concurrent;
using System.Text;
using MailKit.Net.Smtp;
using MailKit.Security;
using System.IO;
using MimeKit;
using Recon.Core.Constants;
using Recon.Core.Factories;
using Recon.Core.Interfaces;
using Recon.Core.Interfaces.Repositories;
using Recon.Core.Models;
using Recon.Core.Options;

namespace Recon.Core.Services;

public class MailService : IMailService
{
    // Events older than this are not mailed (backlog in Cache when the module is switched on)
    private static readonly TimeSpan MaxEventAge = TimeSpan.FromDays(3);
    // Morning report thresholds
    private static readonly TimeSpan NoConnectionAfter = TimeSpan.FromHours(1);
    private static readonly TimeSpan NoDailyAfter = TimeSpan.FromHours(24);

    private readonly IAppLogRepository _appLog;
    private readonly IFileDataRepository _fileDataRepository;
    private readonly IConfigRepository _configRepository;
    private readonly IServerRepository _serverRepository;
    private readonly IUserRepository _userRepository;

    private MailServerConfig _config = new();
    private readonly ConcurrentQueue<FilePair> _queue = new();
    private Task? _sendingTask;
    private Task? _reportTask;
    private CancellationTokenSource? _cts;

    public MailService(IAppLogRepository appLog, IFileDataRepository fileDataRepository,
        IConfigRepository configRepository, IServerRepository serverRepository, IUserRepository userRepository)
    {
        _appLog = appLog;
        _fileDataRepository = fileDataRepository;
        _configRepository = configRepository;
        _serverRepository = serverRepository;
        _userRepository = userRepository;
    }

    public bool IsRunning => _sendingTask is { IsCompleted: false };

    public void StartSendingLoop()
    {
        if (IsRunning) return;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _sendingTask = Task.Run(async () =>
        {
            await EnsureConfigAsync(forceReload: true);
            await SendingLoop(token);
        });
        _reportTask = Task.Run(() => DailyReportLoop(_cts.Token));

        _ = _appLog.LogAsync(LogServiceId.Mail, "mail_start");
    }

    public void StopSendingLoop()
    {
        _cts?.Cancel();
        _cts = null;
        _sendingTask = null;
        _reportTask = null;
        _ = _appLog.LogAsync(LogServiceId.Mail, "mail_stop");
    }

    public void AddToQueue(FilePair pair)
    {
        // Module is off: nobody would drain the queue, it would only grow in memory
        if (!IsRunning) return;

        if (pair is { ReconNumber: > 0, Timestamp.Year: > 2000 } && pair.Timestamp < DateTime.Now - MaxEventAge)
        {
            // Backlog from Cache after the module was switched on: these events are long known
            _ = _appLog.LogAsync(LogServiceId.Mail, "mail_skip_old",
                $"Подія старша за {MaxEventAge.TotalDays:0} дн., не надсилаємо: ReconId={pair.ReconNumber} Object='{pair.Object}' Подія={pair.Timestamp:dd.MM.yyyy HH:mm}");
            return;
        }

        if (pair is { ReconNumber: > 0, Timestamp.Year: > 2000 })
        {
            _queue.Enqueue(pair);
            _ = _appLog.LogAsync(LogServiceId.Mail, "mail_queued",
                $"ReconId={pair.ReconNumber} Object='{pair.Object}' File='{pair.Express?.FileNum ?? pair.Data?.FileNum}' QueueSize={_queue.Count}");
        }
        else
        {
            _ = _appLog.LogAsync(LogServiceId.Mail, "mail_skip",
                $"Файл відхилено: ReconNumber={pair.ReconNumber} Timestamp={pair.Timestamp:yyyy-MM-dd HH:mm:ss}");
        }
    }

    private async Task SendingLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (_queue.IsEmpty)
            {
                await Task.Delay(1000, token).ContinueWith(_ => { });
                continue;
            }

            if (_queue.TryDequeue(out var firstFile))
            {
                var miniBatch = new List<FilePair> { firstFile };
                int currentReconId = firstFile.ReconNumber;
                string objectName = firstFile.Object;

                if (_queue.TryPeek(out var nextFile) && nextFile.ReconNumber == currentReconId)
                {
                    if (_queue.TryDequeue(out var secondFile))
                        miniBatch.Add(secondFile);
                }

                try
                {
                    await _appLog.LogAsync(LogServiceId.Mail, "mail_processing",
                        $"Обробка: ReconId={currentReconId} Object='{objectName}' Файлів={miniBatch.Count}");

                    var moduleConfig = await _configRepository.GetModuleConfigAsync();
                    var recipients = await _fileDataRepository.GetRecipientsByReconIdAsync(
                        currentReconId, objectName, moduleConfig.MailRestrictionEnabled);

                    if (recipients == null || !recipients.Any())
                    {
                        await _appLog.LogAsync(LogServiceId.Mail, "mail_no_recipients",
                            $"Немає отримувачів для ReconId={currentReconId} Object='{objectName}'");
                    }
                    else
                    {
                        await _appLog.LogAsync(LogServiceId.Mail, "mail_recipients_found",
                            $"ReconId={currentReconId}: знайдено {recipients.Count} отримувачів: {string.Join(", ", recipients)}");
                        await SendEmailForGroupAsync(currentReconId, objectName, recipients, miniBatch);
                    }
                }
                catch (Exception ex)
                {
                    await _appLog.LogAsync(LogServiceId.Mail, "mail_error", $"Відправка для {currentReconId}: {ex.Message}");
                }

                await Task.Delay(1000, token).ContinueWith(_ => { });
            }
        }
    }

    private async Task DailyReportLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.Now;
                var nextRun = DateTime.Today.AddHours(7);
                if (now >= nextRun) nextRun = nextRun.AddDays(1);

                var delay = nextRun - now;
                await Task.Delay(delay, token);

                if (token.IsCancellationRequested) break;

                await SendDailyReportAsync();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                await _appLog.LogAsync(LogServiceId.Mail, "mail_error", $"Суточний звіт: {ex.Message}");
                await Task.Delay(TimeSpan.FromHours(1), token).ContinueWith(_ => { });
            }
        }
    }

    private async Task SendDailyReportAsync()
    {
        var recipients = await _userRepository.GetAdminEmailsAsync();
        if (!recipients.Any())
        {
            await _appLog.LogAsync(LogServiceId.Mail, "daily_report_skip", "Немає активних адміністраторів з увімкненою розсилкою");
            return;
        }

        // Only registrators switched on in Properties (sp_GetActiveFtpServers)
        var enabled = (await _serverRepository.GetAllServersAsync())
            .Select(s => (s.Unit, s.Substation, s.Object))
            .ToHashSet();
        var allRows = await _serverRepository.GetDailyReportAsync(DateTime.Today);
        var rows = allRows.Where(r => enabled.Contains((r.Unit, r.Substation, r.Object))).ToList();
        if (rows.Count == 0 && allRows.Count > 0)
        {
            // Names in sp_GetDailyReport and sp_GetActiveFtpServers didn't match — better a full report than none
            await _appLog.LogAsync(LogServiceId.Mail, "daily_report_warn",
                "Не вдалося зіставити звіт з увімкненими реєстраторами, надсилаємо по всіх");
            rows = allRows;
        }
        if (!rows.Any()) return;

        var moduleConfig = await _configRepository.GetModuleConfigAsync();
        string subject = $"Стан серверів і реєстраторів на {DateTime.Now:dd.MM.yyyy HH:mm}";
        string body = BuildDailyReportText(rows, moduleConfig.IsFtpActive);

        await SendToAllAsync(recipients, subject, body);
        await _appLog.LogAsync(LogServiceId.Mail, "daily_report_sent",
            $"Отримувачі: {string.Join(", ", recipients)}. Реєстраторів: {rows.Count}");
    }

    private static string BuildDailyReportText(List<DailyReportRow> rows, bool isFtpActive)
    {
        DateTime now = DateTime.Now;
        var noConnection = rows.Where(r => r.LastPing is null || r.LastPing < now - NoConnectionAfter).ToList();
        var noDaily = rows.Where(r => r.LastDaily is null || r.LastDaily < now - NoDailyAfter).ToList();

        static string Name(DailyReportRow r) => $"{r.Unit} / {r.Substation} / {r.Object}";
        static string Ago(DateTime? t) => t.HasValue ? $"останній раз {t:dd.MM.yyyy HH:mm}" : "жодного разу";

        var sb = new StringBuilder();
        sb.AppendLine($"СТАН СЕРВЕРІВ І РЕЄСТРАТОРІВ на {now:dd.MM.yyyy HH:mm}");
        sb.AppendLine($"Увімкнених реєстраторів: {rows.Count}");
        sb.AppendLine();

        if (!isFtpActive)
        {
            sb.AppendLine("⚠ Модуль «Збір FTP» вимкнено — дані про зв'язок і DAILY можуть бути неактуальні.");
            sb.AppendLine();
        }

        sb.AppendLine($"НЕМАЄ ЗВ'ЯЗКУ (більше {NoConnectionAfter.TotalHours:0} год.): {noConnection.Count}");
        foreach (var r in noConnection.OrderBy(Name))
            sb.AppendLine($"  • {Name(r)} — {Ago(r.LastPing)}");
        if (noConnection.Count == 0) sb.AppendLine("  — усі на зв'язку");
        sb.AppendLine();

        sb.AppendLine($"НЕМАЄ DAILY (більше {NoDailyAfter.TotalHours:0} год.): {noDaily.Count}");
        foreach (var r in noDaily.OrderBy(Name))
            sb.AppendLine($"  • {Name(r)} — {Ago(r.LastDaily)}");
        if (noDaily.Count == 0) sb.AppendLine("  — від усіх отримано");

        sb.AppendLine();
        sb.AppendLine("Це повідомлення сформовано автоматично.");
        return sb.ToString();
    }

    // "Повідомити всіх" can send while the module is off, so settings are loaded on demand
    private async Task<bool> EnsureConfigAsync(bool forceReload = false)
    {
        if (forceReload || string.IsNullOrEmpty(_config.SmtpServer))
        {
            try
            {
                _config = await _configRepository.GetMailServerConfigAsync() ?? new MailServerConfig();
            }
            catch (Exception ex)
            {
                await _appLog.LogAsync(LogServiceId.Mail, "mail_error", $"Завантаження налаштувань SMTP: {ex.Message}");
            }
        }

        if (!string.IsNullOrEmpty(_config.SmtpServer)) return true;

        await _appLog.LogAsync(LogServiceId.Mail, "mail_error",
            "Відсутні налаштування SMTP (access_settings → mail → \"SMTP server\")");
        return false;
    }

    public async Task SendToAllAsync(IEnumerable<string> recipients, string subject, string body, IEnumerable<string>? attachments = null)
    {
        if (!await EnsureConfigAsync()) return;

        const int batchSize = 50;
        var bodyBuilder = new BodyBuilder { TextBody = body };
        AddAttachments(bodyBuilder, attachments);
        var messageBody = bodyBuilder.ToMessageBody();

        try
        {
            using var client = new SmtpClient();
            client.CheckCertificateRevocation = false;
            await client.ConnectAsync(_config.SmtpServer, _config.Port,
                _config.UseSsl ? SecureSocketOptions.Auto : SecureSocketOptions.None);
            if (!string.IsNullOrEmpty(_config.AuthLogin))
                await client.AuthenticateAsync(_config.AuthLogin, _config.AuthPassword);

            foreach (string[] batch in recipients.Chunk(batchSize))
            {
                var message = new MimeMessage();
                message.From.Add(new MailboxAddress(_config.NameSender, _config.EmailSender));
                // Recipients go in Bcc; the sender's own mailbox is not added to To (it got every file)
                message.To.Add(new GroupAddress("Undisclosed recipients"));
                message.Subject = subject;
                message.Body = messageBody;

                foreach (var email in batch)
                {
                    if (MailboxAddress.TryParse(email, out var address))
                        message.Bcc.Add(address);
                }

                if (message.Bcc.Count == 0) continue;

                try
                {
                    await client.SendAsync(message);
                }
                catch (SmtpCommandException ex) when (
                    ex.ErrorCode == SmtpErrorCode.RecipientNotAccepted ||
                    ex.ErrorCode == SmtpErrorCode.MessageNotAccepted)
                {
                    await SendOneByOneAsync(client, message, subject, messageBody);
                }
                catch (Exception ex)
                {
                    await _appLog.LogAsync(LogServiceId.Mail, "mail_error", $"Пакет: {ex.Message}");
                }
            }

            await client.DisconnectAsync(true);
        }
        catch (Exception ex)
        {
            await _appLog.LogAsync(LogServiceId.Mail, "mail_error", $"SMTP з'єднання: {ex.Message}");
        }
    }

    public async Task SendEmailAsync(string toEmail, string subject, string body, IEnumerable<string>? attachments = null)
        => await SendToAllAsync(new[] { toEmail }, subject, body, attachments);

    public async Task SendDailyFileAsync(ServerInfo server, string filePath)
    {
        // Snapshot before the first await: integration moves the file out of Cache within seconds
        string tempCopy = Path.Combine(Path.GetTempPath(), $"recon_{Guid.NewGuid():N}", Path.GetFileName(filePath));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(tempCopy)!);
            File.Copy(filePath, tempCopy);
        }
        catch (Exception ex)
        {
            await _appLog.LogAsync(LogServiceId.Mail, "mail_error", $"DAILY {server.Object}: не вдалося скопіювати файл: {ex.Message}");
            return;
        }

        try
        {
            // sp_GetActiveFtpServers may not return recon_id; the file name always carries it (DAILY353.xxx)
            int reconId = server.ReconId > 0 ? server.ReconId : BaseFileFactory.Create(filePath)?.ReconNumber ?? 0;

            var moduleConfig = await _configRepository.GetModuleConfigAsync();
            var recipients = await _fileDataRepository.GetRecipientsByReconIdAsync(
                reconId, server.Object, moduleConfig.MailRestrictionEnabled);
            if (!recipients.Any())
            {
                await _appLog.LogAsync(LogServiceId.Mail, "mail_no_recipients",
                    $"DAILY: немає отримувачів для ReconId={reconId} Object='{server.Object}'");
                return;
            }

            string subject = $"Суточний файл: {server.Substation} - {server.Object} | {DateTime.Today:dd.MM.yyyy}";
            string body = $"Прийшов суточний файл.\n\nОб'єкт: {server.Substation} / {server.Object}\n\nЦе повідомлення сформовано автоматично.";

            await SendToAllAsync(recipients, subject, body, new[] { tempCopy });
            await _appLog.LogAsync(LogServiceId.Mail, "mail_sent",
                $"DAILY {server.Substation}/{server.Object} → {string.Join(", ", recipients)}");
        }
        catch (Exception ex)
        {
            await _appLog.LogAsync(LogServiceId.Mail, "mail_error", $"DAILY {server.Object}: {ex.Message}");
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(tempCopy)!, recursive: true); } catch { }
        }
    }

    private async Task SendEmailForGroupAsync(int reconId, string objectName, List<string> recipients, List<FilePair> files)
    {
        if (!files.Any()) return;
        if (!await EnsureConfigAsync()) return;

        var firstFile = files.First();
        string displayObj = string.IsNullOrEmpty(objectName) ? $"Об'єкт {reconId}" : objectName;

        // Cap timestamp at now to avoid showing future times from device clock drift
        DateTime displayTime = firstFile.Timestamp > DateTime.Now ? DateTime.Now : firstFile.Timestamp;
        string subject = $"Аварійні файли: {firstFile.Substation} - {displayObj} | {displayTime:dd.MM.yyyy HH:mm}";

        var sb = new StringBuilder();
        sb.AppendLine(_config.MsgTemplate);
        sb.AppendLine($"Надсилаємо файли подій по об'єкту: {firstFile.Substation} / {displayObj}");
        sb.AppendLine($"Всього подій: {files.Count}");
        sb.AppendLine($"Дата останньої події: {files.Max(f => f.Timestamp):dd.MM.yyyy HH:mm}");
        sb.AppendLine();
        sb.AppendLine("Це повідомлення сформовано автоматично.");

        var bodyBuilder = new BodyBuilder { TextBody = sb.ToString() };

        var pathsToSend = new List<string>();
        foreach (var pair in files)
        {
            if (pair.Express != null && !string.IsNullOrEmpty(pair.Express.FullPath))
                pathsToSend.Add(pair.Express.FullPath);
            if (pair.Data != null && !string.IsNullOrEmpty(pair.Data.FullPath))
                pathsToSend.Add(pair.Data.FullPath);
        }
        AddAttachments(bodyBuilder, pathsToSend);

        var messageBody = bodyBuilder.ToMessageBody();
        try
        {
            await _appLog.LogAsync(LogServiceId.Mail, "mail_smtp_connect",
                $"Підключення до {_config.SmtpServer}:{_config.Port} SSL={_config.UseSsl}");

            using var client = new SmtpClient();
            client.CheckCertificateRevocation = false;
            await client.ConnectAsync(_config.SmtpServer, _config.Port,
                _config.UseSsl ? SecureSocketOptions.Auto : SecureSocketOptions.None);
            if (_config.UseAuth)
                await client.AuthenticateAsync(_config.AuthLogin, _config.AuthPassword);

            await _appLog.LogAsync(LogServiceId.Mail, "mail_smtp_ok",
                $"SMTP з'єднання встановлено для {displayObj}");

            int totalSent = 0;
            foreach (string[] userBatch in recipients.Chunk(50))
            {
                var message = new MimeMessage();
                message.From.Add(new MailboxAddress(_config.NameSender, _config.EmailSender));
                // Recipients go in Bcc; the sender's own mailbox is not added to To (it got every file)
                message.To.Add(new GroupAddress("Undisclosed recipients"));
                message.Subject = subject;
                message.Body = messageBody;

                var invalidEmails = new List<string>();
                foreach (var email in userBatch)
                {
                    if (MailboxAddress.TryParse(email, out var address))
                        message.Bcc.Add(address);
                    else
                        invalidEmails.Add(email);
                }

                if (invalidEmails.Any())
                    await _appLog.LogAsync(LogServiceId.Mail, "mail_invalid_email",
                        $"Невалідні адреси (не email?): {string.Join(", ", invalidEmails)}");

                if (message.Bcc.Count == 0)
                {
                    await _appLog.LogAsync(LogServiceId.Mail, "mail_no_valid_emails",
                        $"Пакет для {displayObj}: всі адреси невалідні, лист не відправлено");
                    continue;
                }

                try
                {
                    await client.SendAsync(message);
                    totalSent += message.Bcc.Count;
                }
                catch (SmtpCommandException ex) when (
                    ex.ErrorCode == SmtpErrorCode.RecipientNotAccepted ||
                    ex.ErrorCode == SmtpErrorCode.MessageNotAccepted)
                {
                    await SendOneByOneAsync(client, message, subject, messageBody);
                }
                catch (Exception ex)
                {
                    await _appLog.LogAsync(LogServiceId.Mail, "mail_error", $"Пакет для {displayObj}: {ex.Message}");
                }
            }

            await client.DisconnectAsync(true);
            await _appLog.LogAsync(LogServiceId.Mail, "mail_sent",
                $"Надіслано для {displayObj}: {totalSent} отримувачів. Тема: {subject}");
        }
        catch (Exception ex)
        {
            await _appLog.LogAsync(LogServiceId.Mail, "mail_error", $"SMTP для {displayObj}: {ex.Message}");
        }
    }

    private async Task SendOneByOneAsync(SmtpClient client, MimeMessage originalMessage, string subject, MimeEntity messageBody)
    {
        foreach (var internetAddress in originalMessage.Bcc.ToList())
        {
            if (internetAddress is not MailboxAddress recipient) continue;
            try
            {
                var single = new MimeMessage();
                single.From.Add(new MailboxAddress(_config.NameSender, _config.EmailSender));
                single.To.Add(recipient);
                single.Subject = subject;
                single.Body = messageBody;
                await client.SendAsync(single);
            }
            catch (SmtpCommandException ex)
            {
                await _appLog.LogAsync(LogServiceId.Mail, "invalid_recipient",
                    $"{recipient.Address}: {ex.Message} (код: {ex.ErrorCode})");
            }
            catch (Exception ex)
            {
                await _appLog.LogAsync(LogServiceId.Mail, "mail_error", $"{recipient.Address}: {ex.Message}");
            }
        }
    }

    private void AddAttachments(BodyBuilder bodyBuilder, IEnumerable<string>? attachments)
    {
        if (attachments == null) return;
        foreach (var path in attachments)
        {
            if (!File.Exists(path)) continue;
            try { bodyBuilder.Attachments.Add(path); }
            catch { }
        }
    }

    // Legacy Stop() kept for compatibility
    public void Stop() => StopSendingLoop();
}
