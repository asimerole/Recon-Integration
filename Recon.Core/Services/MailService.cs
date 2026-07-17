using System.Collections.Concurrent;
using System.Text;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using System.IO;
using MimeKit;
using Recon.Core.Interfaces;
using Recon.Core.Interfaces.Repositories;
using Recon.Core.Models;
using Recon.Core.Options;

namespace Recon.Core.Services;

public class MailService : IMailService
{
    private readonly ILogger<MailService> _logger;
    private readonly IConfigRepository _configRepository;
    private readonly IFileDataRepository _fileDataRepository;

    private MailServerConfig? _config;

    private readonly ConcurrentQueue<FilePair> _queue = new();
    private Task? _sendingTask;
    private CancellationTokenSource _cts = new();

    public MailService(ILogger<MailService> logger, IConfigRepository configRepository, IFileDataRepository fileDataRepository)
    {
        _logger = logger;
        _configRepository = configRepository;
        _fileDataRepository = fileDataRepository;
    }

    public void StartSendingLoop()
    {
        if (_sendingTask != null) return;
        _sendingTask = Task.Run(SendingLoop);
    }

    public void AddToQueue(FilePair pair)
    {
        if (pair is { ReconNumber: > 0, Timestamp.Year: > 2000 })
            _queue.Enqueue(pair);
    }

    private async Task SendingLoop()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            if (_queue.IsEmpty)
            {
                await Task.Delay(1000, _cts.Token);
                continue;
            }

            if (_queue.TryDequeue(out var firstFile))
            {
                var miniBatch = new List<FilePair> { firstFile };
                int currentReconId = firstFile.ReconNumber;

                if (_queue.TryPeek(out var nextFile) && nextFile.ReconNumber == currentReconId)
                {
                    if (_queue.TryDequeue(out var secondFile))
                        miniBatch.Add(secondFile);
                }

                try
                {
                    var recipients = await _fileDataRepository.GetRecipientsByReconIdAsync(currentReconId);
                    if (recipients.Any())
                    {
                        await SendEmailForGroupAsync(currentReconId, recipients, miniBatch);
                        _logger.LogInformation("Відправлено {Count} файлів для об'єкта {Id}", miniBatch.Count, currentReconId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Помилка відправки пошти для {Id}", currentReconId);
                }

                await Task.Delay(1000, _cts.Token);
            }
        }
    }

    public async Task SendToAllAsync(IEnumerable<string> recipients, string subject, string body, IEnumerable<string>? attachments = null)
    {
        await EnsureConfigAsync();
        if (string.IsNullOrEmpty(_config!.SmtpServer))
        {
            _logger.LogError("Немає налаштувань SMTP сервера!");
            return;
        }

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

            foreach (string[] batch in recipients.Chunk(50))
            {
                try
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
                        else
                            _logger.LogWarning("Некоректний email: {Email}", email);
                    }

                    if (message.Bcc.Count > 0)
                    {
                        await client.SendAsync(message);
                        _logger.LogInformation("Пачка листів ({Count} шт) відправлена.", message.Bcc.Count);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Помилка відправки на групу адресатів");
                }
            }

            await client.DisconnectAsync(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Критична помилка SMTP підключення");
        }
    }

    public async Task SendEmailAsync(string toEmail, string subject, string body, IEnumerable<string>? attachments = null)
    {
        await SendToAllAsync(new[] { toEmail }, subject, body, attachments);
    }

    public async Task ProcessBatchAsync(List<FilePair> batch)
    {
        if (batch == null || !batch.Any()) return;

        var groupedFiles = batch
            .Where(f => f.ReconNumber > 0)
            .GroupBy(f => f.ReconNumber);

        foreach (var group in groupedFiles)
        {
            int reconId = group.Key;
            var recipients = await _fileDataRepository.GetRecipientsByReconIdAsync(reconId);

            if (!recipients.Any())
            {
                _logger.LogWarning("Не знайдено отримувачів для об'єкта ID={Id}. Пропуск.", reconId);
                continue;
            }

            var freshFiles = group.Where(f => f.Timestamp > DateTime.Now.AddDays(-3)).ToList();
            if (!freshFiles.Any()) continue;

            try
            {
                await SendEmailForGroupAsync(reconId, recipients, freshFiles);
                await Task.Delay(1000);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Помилка відправки пошти для об'єкта ID={Id}", reconId);
            }
        }
    }

    public void Stop() => _cts.Cancel();

    private async Task SendEmailForGroupAsync(int reconId, List<string> recipients, List<FilePair> files)
    {
        if (!files.Any()) return;
        await EnsureConfigAsync();
        if (string.IsNullOrEmpty(_config!.SmtpServer))
        {
            _logger.LogError("Немає налаштувань SMTP сервера!");
            return;
        }

        var firstFile = files.First();
        string objectName = firstFile.Object ?? $"Об'єкт {reconId}";
        string subject = $"Аварійні файли: {firstFile.Substation} - {objectName} | Час: {firstFile.Timestamp}";

        var sb = new StringBuilder();
        sb.AppendLine(_config.MsgTemplate);
        sb.AppendLine($"Надсилаємо файли подій по об'єкту: {firstFile.Substation}");
        sb.AppendLine($"Всього подій: {files.Count}");
        sb.AppendLine($"Дата останньої події: {files.Max(f => f.Timestamp)}");
        sb.AppendLine();
        sb.AppendLine("Це повідомлення сформовано автоматично.");

        var bodyBuilder = new BodyBuilder { TextBody = sb.ToString() };

        var pathsToSend = files
            .SelectMany(p => new[] { p.Express?.FullPath, p.Data?.FullPath })
            .Where(path => !string.IsNullOrEmpty(path))
            .Cast<string>()
            .ToList();

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
                try
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
                        else
                            _logger.LogWarning("Некоректний email: {Email}", email);
                    }

                    if (message.Bcc.Count == 0) continue;

                    try
                    {
                        await client.SendAsync(message);
                        _logger.LogInformation("Успішно відправлено пакет для {Count} отримувачів.", message.Bcc.Count);
                    }
                    catch (SmtpCommandException ex) when (
                        ex.ErrorCode == SmtpErrorCode.RecipientNotAccepted ||
                        ex.ErrorCode == SmtpErrorCode.MessageNotAccepted)
                    {
                        _logger.LogWarning("Помилка пакетної відправки — пробуємо поштучно: {Msg}", ex.Message);
                        await SendIndividuallyAsync(client, message, subject, messageBody);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Помилка відправки групи листів");
                }
            }

            await client.DisconnectAsync(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Критична помилка SMTP для об'єкта {Obj}", objectName);
        }
    }

    private async Task SendIndividuallyAsync(SmtpClient client, MimeMessage original, string subject, MimeEntity body)
    {
        foreach (var addr in original.Bcc.OfType<MailboxAddress>())
        {
            try
            {
                var msg = new MimeMessage();
                msg.From.AddRange(original.From);
                msg.To.Add(addr);
                msg.Subject = subject;
                msg.Body = body;
                await client.SendAsync(msg);
            }
            catch (Exception ex)
            {
                _logger.LogError("Не вдалося відправити на {Email}: {Msg}", addr.Address, ex.Message);
            }
        }
    }

    private void AddAttachments(BodyBuilder bodyBuilder, IEnumerable<string>? attachments)
    {
        if (attachments == null) return;
        foreach (var path in attachments)
        {
            if (File.Exists(path))
            {
                try { bodyBuilder.Attachments.Add(path); }
                catch (Exception ex) { _logger.LogError(ex, "Не вдалося прикріпити файл {Path}", path); }
            }
            else
            {
                _logger.LogWarning("Файл для вкладення не знайдено: {Path}", path);
            }
        }
    }

    private async Task EnsureConfigAsync()
    {
        _config ??= await _configRepository.GetMailServerConfigAsync();
    }
}
