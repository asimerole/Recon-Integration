using System.Collections.Concurrent;
using System.Text;
using MailKit.Net.Smtp;
using MailKit.Security;
using System.IO;
using MimeKit;
using Recon.Core.Constants;
using Recon.Core.Interfaces;
using Recon.Core.Interfaces.Repositories;
using Recon.Core.Models;
using Recon.Core.Options;

namespace Recon.Core.Services;

public class MailService : IMailService
{
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

    public void StartSendingLoop()
    {
        if (_sendingTask != null && !_sendingTask.IsCompleted) return;

        _cts = new CancellationTokenSource();
        _sendingTask = Task.Run(async () =>
        {
            _config = await _configRepository.GetMailServerConfigAsync();
            await SendingLoop(_cts.Token);
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
        if (pair is { ReconNumber: > 0, Timestamp.Year: > 2000 })
            _queue.Enqueue(pair);
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
                    var recipients = await _fileDataRepository.GetRecipientsByReconIdAsync(currentReconId, objectName);
                    if (recipients != null && recipients.Any())
                        await SendEmailForGroupAsync(currentReconId, objectName, recipients, miniBatch);
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
        var rows = await _serverRepository.GetDailyReportAsync(DateTime.Today);
        if (!rows.Any()) return;

        var recipients = await _userRepository.GetAllUserEmailsAsync();
        if (!recipients.Any()) return;

        string subject = $"Звіт по стану каналів зв'язку за {DateTime.Today:dd.MM.yyyy}";
        string body = BuildDailyReportText(rows);

        await SendToAllAsync(recipients, subject, body);
        await _appLog.LogAsync(LogServiceId.Mail, "daily_report_sent", $"Отримувачів: {recipients.Count}, Об'єктів: {rows.Count}");
    }

    private static string BuildDailyReportText(List<DailyReportRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"ЗВІТ ПО СТАНУ КАНАЛІВ ЗВ'ЯЗКУ за {DateTime.Today:dd.MM.yyyy}");
        sb.AppendLine(new string('═', 70));
        sb.AppendLine();
        sb.AppendLine($"{"Підстанція",-20} {"Об'єкт",-20} {"Пінг",-10} {"RECON",-10} {"Зібрано",-8}");
        sb.AppendLine(new string('─', 70));

        foreach (var r in rows)
        {
            string ping = r.HasPingToday ? r.LastPing!.Value.ToString("HH:mm") : "⚠ немає";
            string recon = r.LastRecon.HasValue && r.LastRecon.Value.Date == DateTime.Today
                ? r.LastRecon.Value.ToString("HH:mm") : "-";
            sb.AppendLine($"{r.Substation,-20} {r.Object,-20} {ping,-10} {recon,-10} {r.Collected,-8}");
        }

        var newlyDead = rows.Where(r => r.IsNewlyDead).ToList();
        var restored = rows.Where(r => r.IsRestored).ToList();

        if (newlyDead.Any() || restored.Any())
        {
            sb.AppendLine();
            sb.AppendLine(new string('═', 70));
            sb.AppendLine("ЗМІНИ ВІДНОСНО ВЧОРА:");
            foreach (var r in newlyDead)
                sb.AppendLine($"  ↓ Зникли: {r.Substation}/{r.Object} (вчора була активність)");
            foreach (var r in restored)
                sb.AppendLine($"  ↑ Відновились: {r.Substation}/{r.Object}");
        }

        sb.AppendLine();
        sb.AppendLine("Це повідомлення сформовано автоматично.");
        return sb.ToString();
    }

    public async Task SendToAllAsync(IEnumerable<string> recipients, string subject, string body, IEnumerable<string>? attachments = null)
    {
        if (string.IsNullOrEmpty(_config.SmtpServer))
        {
            await _appLog.LogAsync(LogServiceId.Mail, "mail_error", "Відсутні налаштування SMTP");
            return;
        }

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
                message.To.Add(new MailboxAddress("Користувачі", _config.EmailSender));
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

    public async Task ProcessBatchAsync(List<FilePair> batch)
    {
        if (batch == null || !batch.Any()) return;

        var groupedFiles = batch
            .Where(f => f.ReconNumber > 0)
            .GroupBy(f => f.ReconNumber);

        foreach (var group in groupedFiles)
        {
            int reconId = group.Key;
            var filesInGroup = group.ToList();
            string objectName = filesInGroup.First().Object;

            var recipients = await _fileDataRepository.GetRecipientsByReconIdAsync(reconId, objectName);
            if (recipients == null || !recipients.Any()) continue;

            var freshFiles = filesInGroup
                .Where(f => f.Timestamp > DateTime.Now.AddDays(-3))
                .ToList();
            if (!freshFiles.Any()) continue;

            try
            {
                await SendEmailForGroupAsync(reconId, objectName, recipients, freshFiles);
                await Task.Delay(1000);
            }
            catch (Exception ex)
            {
                await _appLog.LogAsync(LogServiceId.Mail, "mail_error", $"ProcessBatch для {reconId}: {ex.Message}");
            }
        }
    }

    private async Task SendEmailForGroupAsync(int reconId, string objectName, List<string> recipients, List<FilePair> files)
    {
        if (!files.Any()) return;
        if (string.IsNullOrEmpty(_config.SmtpServer))
        {
            await _appLog.LogAsync(LogServiceId.Mail, "mail_error", "Відсутні налаштування SMTP");
            return;
        }

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
            using var client = new SmtpClient();
            client.CheckCertificateRevocation = false;
            await client.ConnectAsync(_config.SmtpServer, _config.Port,
                _config.UseSsl ? SecureSocketOptions.Auto : SecureSocketOptions.None);
            if (!string.IsNullOrEmpty(_config.AuthLogin))
                await client.AuthenticateAsync(_config.AuthLogin, _config.AuthPassword);

            foreach (string[] userBatch in recipients.Chunk(50))
            {
                var message = new MimeMessage();
                message.From.Add(new MailboxAddress(_config.NameSender, _config.EmailSender));
                message.To.Add(new MailboxAddress("Користувачі", _config.EmailSender));
                message.Subject = subject;
                message.Body = messageBody;

                foreach (var email in userBatch)
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
                    await _appLog.LogAsync(LogServiceId.Mail, "mail_error", $"Пакет для {displayObj}: {ex.Message}");
                }
            }

            await client.DisconnectAsync(true);
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
