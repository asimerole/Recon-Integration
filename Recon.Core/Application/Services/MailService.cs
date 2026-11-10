using System.IO;
using System.Text;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Recon.Core.Application.Interfaces;
using Recon.Core.Application.Interfaces.Repositories;
using Recon.Core.Application.Options;
using Recon.Core.Domain.Constants;
using Recon.Core.Domain.Factories;
using Recon.Core.Domain.Models;

namespace Recon.Core.Application.Services;

/// <summary>
/// Event e-mails go through the persistent mail_outbox:
///   integration → AddToQueueAsync → mail_outbox → (object quiet for 30 s) → one e-mail per object burst.
/// A failed send stays in the outbox and is retried (1, 2, 4 … 60 min) for up to a day.
/// DAILY files, the morning report and "Повідомити всіх" are sent immediately via <see cref="SendToAllAsync"/>.
/// </summary>
public class MailService : IMailService
{
    // Events older than this are not mailed (backlog in Cache when the module is switched on)
    private static readonly TimeSpan MaxEventAge = TimeSpan.FromDays(3);
    // Outbox: wait until an object is quiet for Settle, but never longer than MaxWait
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromDays(1);
    private static readonly TimeSpan ConfigTtl = TimeSpan.FromMinutes(1);
    private const int OutboxKeepDays = 30;
    // Morning report thresholds
    private static readonly TimeSpan NoConnectionAfter = TimeSpan.FromHours(1);
    private static readonly TimeSpan NoDailyAfter = TimeSpan.FromHours(24);

    private readonly IAppLogRepository _appLog;
    private readonly IFileDataRepository _fileDataRepository;
    private readonly IConfigRepository _configRepository;
    private readonly IServerRepository _serverRepository;
    private readonly IUserRepository _userRepository;
    private readonly IMailOutboxRepository _outbox;

    private MailServerConfig _config = new();
    private DateTime _configLoadedAt;
    private bool _smtpMissingLogged;

    private readonly object _stateLock = new();
    private CancellationTokenSource? _cts;

    public MailService(IAppLogRepository appLog, IFileDataRepository fileDataRepository,
        IConfigRepository configRepository, IServerRepository serverRepository, IUserRepository userRepository,
        IMailOutboxRepository outbox)
    {
        _appLog = appLog;
        _fileDataRepository = fileDataRepository;
        _configRepository = configRepository;
        _serverRepository = serverRepository;
        _userRepository = userRepository;
        _outbox = outbox;
    }

    public bool IsRunning => _cts is { IsCancellationRequested: false };

    public void StartSendingLoop()
    {
        lock (_stateLock)
        {
            if (IsRunning) return;
            _cts = new CancellationTokenSource();
        }

        var token = _cts.Token;
        Task.Run(() => OutboxLoopAsync(token));
        Task.Run(() => DailyReportLoopAsync(token));
        _ = _appLog.LogAsync(LogServiceId.Mail, "mail_start");
    }

    public void StopSendingLoop()
    {
        lock (_stateLock)
        {
            if (!IsRunning) return;
            _cts!.Cancel();
            _cts = null;
        }
        // Pending rows stay in mail_outbox and go out after the module is switched on again
        _ = _appLog.LogAsync(LogServiceId.Mail, "mail_stop");
    }

    // ───────────── queueing ─────────────

    public async Task AddToQueueAsync(FilePair pair)
    {
        // Module is off: events of this period are not mailed later (customer wants only fresh files)
        if (!IsRunning) return;

        // "Аварійні файли" are RECON/REXPR; DAILY goes separately from FTP, others are not mailed
        var files = new[] { pair.Data?.FullPath, pair.Express?.FullPath }
            .Where(p => !string.IsNullOrEmpty(p) && File.Exists(p))
            .Cast<string>()
            .ToList();
        if (files.Count == 0) return;

        if (pair.ReconNumber <= 0 || pair.Timestamp.Year <= 2000)
        {
            await _appLog.LogAsync(LogServiceId.Mail, "mail_skip",
                $"Файл відхилено: ReconNumber={pair.ReconNumber} Timestamp={pair.Timestamp:yyyy-MM-dd HH:mm:ss}");
            return;
        }

        if (pair.Timestamp < DateTime.Now - MaxEventAge)
        {
            await _appLog.LogAsync(LogServiceId.Mail, "mail_skip_old",
                $"Подія старша за {MaxEventAge.TotalDays:0} дн., не надсилаємо: ReconId={pair.ReconNumber} Object='{pair.Object}' Подія={pair.Timestamp:dd.MM.yyyy HH:mm}");
            return;
        }

        try
        {
            bool queued = await _outbox.EnqueueAsync(pair.ReconNumber, pair.Object, pair.Substation, pair.Timestamp, files);
            await _appLog.LogAsync(LogServiceId.Mail, queued ? "mail_queued" : "mail_duplicate",
                $"ReconId={pair.ReconNumber} Object='{pair.Object}' Файли: {string.Join(", ", files.Select(Path.GetFileName))}");
        }
        catch (Exception ex)
        {
            await _appLog.LogAsync(LogServiceId.Mail, "mail_error", $"Запис у чергу ReconId={pair.ReconNumber}: {ex.Message}");
        }
    }

    // ───────────── outbox processing ─────────────

    private async Task OutboxLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await ProcessOutboxAsync(token);
                await Task.Delay(PollInterval, token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                await _appLog.LogAsync(LogServiceId.Mail, "mail_error", $"Черга: {ex.Message}");
                await Task.Delay(TimeSpan.FromMinutes(1), token).ContinueWith(_ => { });
            }
        }
    }

    private async Task ProcessOutboxAsync(CancellationToken token)
    {
        var due = await _outbox.GetDueAsync(Settle, MaxWait);
        if (due.Count == 0) return;
        // No SMTP settings: rows stay pending (attempts not burned) until the settings appear
        if (!await EnsureConfigAsync()) return;

        bool restricted = (await _configRepository.GetModuleConfigAsync()).MailRestrictionEnabled;
        TolerantSmtpClient? client = null;
        try
        {
            foreach (var group in due.GroupBy(i => (i.ReconId, i.Object)))
            {
                token.ThrowIfCancellationRequested();
                var items = await SkipStaleAsync(group.ToList());
                if (items.Count == 0) continue;

                var (recipients, invalid) = MailComposer.ParseRecipients(
                    await _fileDataRepository.GetRecipientsByReconIdAsync(group.Key.ReconId, group.Key.Object, restricted));
                if (invalid.Count > 0)
                    await _appLog.LogAsync(LogServiceId.Mail, "mail_invalid_email",
                        $"Не email, пропущено: {string.Join(", ", invalid)}");
                if (recipients.Count == 0)
                {
                    await MarkAllAsync(items, MailOutboxStatus.Skipped, error: "немає отримувачів");
                    await _appLog.LogAsync(LogServiceId.Mail, "mail_no_recipients",
                        $"Немає отримувачів для ReconId={group.Key.ReconId} Object='{group.Key.Object}'");
                    continue;
                }

                foreach (var batch in MailComposer.BuildBatches(items, FileSize))
                {
                    try
                    {
                        client ??= await OpenClientAsync(token);
                        await SendEventBatchAsync(client, batch, recipients, token);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        await MarkForRetryAsync(batch, ex.Message);
                        // Broken connection or server down: the rest of this pass would fail the same way
                        if (client is not { IsConnected: true })
                        {
                            client?.Dispose();
                            client = null;
                            return;
                        }
                    }
                }
            }
        }
        finally
        {
            if (client != null)
            {
                try { if (client.IsConnected) await client.DisconnectAsync(true, CancellationToken.None); } catch { }
                client.Dispose();
            }
        }
    }

    private async Task<List<MailOutboxItem>> SkipStaleAsync(List<MailOutboxItem> items)
    {
        var fresh = new List<MailOutboxItem>();
        foreach (var item in items)
        {
            if (item.EventTime >= DateTime.Now - MaxEventAge) { fresh.Add(item); continue; }
            await _outbox.MarkAsync(item.Id, MailOutboxStatus.Skipped, error: "подія застаріла до відправки");
        }
        return fresh;
    }

    private async Task SendEventBatchAsync(TolerantSmtpClient client, List<MailOutboxItem> batch,
        List<MailboxAddress> recipients, CancellationToken token)
    {
        var missing = batch.SelectMany(i => i.Files).Where(f => !File.Exists(f)).ToList();
        var bodyBuilder = new BodyBuilder { TextBody = MailComposer.EventBody(batch, _config.MsgTemplate, missing) };
        foreach (var file in batch.SelectMany(i => i.Files).Except(missing))
            bodyBuilder.Attachments.Add(file);

        string subject = MailComposer.EventSubject(batch);
        var result = await SendAsync(client, recipients, subject, bodyBuilder.ToMessageBody(), token);

        if (result.Sent == 0)
        {
            // Every address refused (550 …) — permanent, retrying for a day won't help
            string reason = "сервер не прийняв жодного отримувача: " + string.Join("; ", result.Rejected);
            await MarkAllAsync(batch, MailOutboxStatus.Failed, error: reason);
            await _appLog.LogAsync(LogServiceId.Mail, "mail_error", $"{subject}: {reason}");
            return;
        }

        string sentTo = string.Join(", ", recipients.Select(r => r.Address).Except(result.Rejected.Select(AddressOf)));
        await MarkAllAsync(batch, MailOutboxStatus.Sent, recipients: sentTo);

        await _appLog.LogAsync(LogServiceId.Mail, "mail_sent", $"{subject} → {sentTo}");
        if (result.Rejected.Count > 0)
            await _appLog.LogAsync(LogServiceId.Mail, "invalid_recipient", string.Join("; ", result.Rejected));
        if (missing.Count > 0)
            await _appLog.LogAsync(LogServiceId.Mail, "mail_attachment_missing", string.Join(", ", missing));
    }

    private async Task MarkForRetryAsync(List<MailOutboxItem> batch, string error)
    {
        foreach (var item in batch)
        {
            bool giveUp = DateTime.Now - item.CreatedAt > GiveUpAfter;
            await _outbox.MarkAsync(item.Id,
                giveUp ? MailOutboxStatus.Failed : MailOutboxStatus.Pending,
                error: error,
                retryAfter: giveUp ? null : MailComposer.RetryDelay(item.Attempts));
        }
        await _appLog.LogAsync(LogServiceId.Mail, "mail_error",
            $"Відправка {batch[0].Object} ({batch.Count} подій), буде повтор: {error}");
    }

    private async Task MarkAllAsync(IEnumerable<MailOutboxItem> items, MailOutboxStatus status, string? recipients = null, string? error = null)
    {
        foreach (var item in items)
            await _outbox.MarkAsync(item.Id, status, recipients, error);
    }

    private static long FileSize(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    // "addr@x (550 …)" → "addr@x"
    private static string AddressOf(string rejected) => rejected.Split(' ')[0];

    // ───────────── immediate sending ─────────────

    public async Task<MailSendResult> SendToAllAsync(IEnumerable<string> recipients, string subject, string body,
        IEnumerable<string>? attachments = null)
    {
        var (valid, invalid) = MailComposer.ParseRecipients(recipients);
        if (invalid.Count > 0)
            await _appLog.LogAsync(LogServiceId.Mail, "mail_invalid_email", $"Не email, пропущено: {string.Join(", ", invalid)}");
        if (valid.Count == 0)
            return new MailSendResult(0, [], invalid, "немає жодної коректної адреси");
        if (!await EnsureConfigAsync())
            return new MailSendResult(0, [], invalid, "не налаштовано SMTP");

        var bodyBuilder = new BodyBuilder { TextBody = body };
        foreach (var path in attachments ?? [])
            if (File.Exists(path)) bodyBuilder.Attachments.Add(path);

        try
        {
            using var client = await OpenClientAsync(CancellationToken.None);
            var result = await SendAsync(client, valid, subject, bodyBuilder.ToMessageBody(), CancellationToken.None);
            await client.DisconnectAsync(true);

            if (result.Rejected.Count > 0)
                await _appLog.LogAsync(LogServiceId.Mail, "invalid_recipient", string.Join("; ", result.Rejected));
            return result with { Invalid = invalid };
        }
        catch (Exception ex)
        {
            await _appLog.LogAsync(LogServiceId.Mail, "mail_error", $"{subject}: {ex.Message}");
            return new MailSendResult(0, [], invalid, ex.Message);
        }
    }

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

            var result = await SendToAllAsync(recipients, subject, body, [tempCopy]);
            if (result.Success)
                await _appLog.LogAsync(LogServiceId.Mail, "mail_sent",
                    $"DAILY {server.Substation}/{server.Object} → {result.Sent} отримувачів");
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

    // ───────────── SMTP ─────────────

    /// <summary>MailKit throws on the first refused RCPT; this one records it and sends to the rest.</summary>
    private sealed class TolerantSmtpClient : SmtpClient
    {
        public List<string> Rejected { get; } = [];

        protected override void OnRecipientNotAccepted(MimeMessage message, MailboxAddress mailbox, SmtpResponse response) =>
            Rejected.Add($"{mailbox.Address} ({(int)response.StatusCode} {response.Response})");
    }

    private async Task<TolerantSmtpClient> OpenClientAsync(CancellationToken token)
    {
        var client = new TolerantSmtpClient { CheckCertificateRevocation = false, Timeout = 60_000 };
        try
        {
            // ssl=true: 465 → implicit TLS, otherwise STARTTLS if offered (System.Net.Mail in Properties can't do 465)
            await client.ConnectAsync(_config.SmtpServer, _config.Port,
                _config.UseSsl ? SecureSocketOptions.Auto : SecureSocketOptions.None, token);
            // Same rule as Recon-Properties, whose test e-mail the customer verifies settings with
            if (_config.UseAuth && !string.IsNullOrWhiteSpace(_config.AuthLogin))
                await client.AuthenticateAsync(_config.AuthLogin, _config.AuthPassword, token);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private async Task<MailSendResult> SendAsync(TolerantSmtpClient client, IReadOnlyList<MailboxAddress> recipients,
        string subject, MimeEntity body, CancellationToken token)
    {
        int sent = 0;
        var rejected = new List<string>();

        foreach (var chunk in recipients.Chunk(MailComposer.RecipientsPerMessage))
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(_config.NameSender, _config.EmailSender));
            // Recipients go in Bcc; the sender's own mailbox is not added to To (it got every file)
            message.To.Add(new GroupAddress("Undisclosed recipients"));
            message.Bcc.AddRange(chunk);
            message.Subject = subject;
            message.Body = body;

            client.Rejected.Clear();
            await client.SendAsync(message, token);
            rejected.AddRange(client.Rejected);
            sent += chunk.Length - client.Rejected.Count;
        }

        return new MailSendResult(sent, rejected, [], null);
    }

    // Settings are re-read every minute: changes in Properties apply without restarting the module
    private async Task<bool> EnsureConfigAsync()
    {
        if (DateTime.Now - _configLoadedAt >= ConfigTtl || string.IsNullOrEmpty(_config.SmtpServer))
        {
            try
            {
                _config = await _configRepository.GetMailServerConfigAsync() ?? new MailServerConfig();
                _configLoadedAt = DateTime.Now;
            }
            catch (Exception ex)
            {
                await _appLog.LogAsync(LogServiceId.Mail, "mail_error", $"Завантаження налаштувань SMTP: {ex.Message}");
            }
        }

        if (!string.IsNullOrEmpty(_config.SmtpServer))
        {
            _smtpMissingLogged = false;
            return true;
        }

        // Logged once until fixed, the outbox loop asks every 10 s
        if (!_smtpMissingLogged)
        {
            _smtpMissingLogged = true;
            await _appLog.LogAsync(LogServiceId.Mail, "mail_error",
                "Відсутні налаштування SMTP (access_settings → mail → \"SMTP server\")");
        }
        return false;
    }

    // ───────────── morning report ─────────────

    private async Task DailyReportLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var nextRun = DateTime.Today.AddHours(7);
                if (DateTime.Now >= nextRun) nextRun = nextRun.AddDays(1);
                await Task.Delay(nextRun - DateTime.Now, token);

                await SendDailyReportAsync();
                await _outbox.CleanupAsync(OutboxKeepDays);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                await _appLog.LogAsync(LogServiceId.Mail, "mail_error", $"Ранковий звіт: {ex.Message}");
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
        var result = await SendToAllAsync(recipients, subject, BuildDailyReportText(rows, moduleConfig.IsFtpActive));

        await _appLog.LogAsync(LogServiceId.Mail, result.Success ? "daily_report_sent" : "mail_error",
            result.Success
                ? $"Ранковий звіт: {result.Sent} отримувачів, реєстраторів: {rows.Count}"
                : $"Ранковий звіт не надіслано: {result.Error}");
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
}
