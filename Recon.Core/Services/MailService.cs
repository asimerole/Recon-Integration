using System.Collections.Concurrent;
using System.Text;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using MimeKit;
using Recon.Core.Interfaces;
using Recon.Core.Models;
using Recon.Core.Options;

namespace Recon.Core.Services;

public class MailService : IMailService
{
    private readonly ILogger<MailService> _logger;
    private readonly MailServerConfig _config;
    private readonly IDatabaseService _databaseService;
    private readonly Dictionary<int, List<string>> _recipientsMap;
    
    private readonly ConcurrentQueue<FilePair> _queue = new();
    private Task _sendingTask;
    private CancellationTokenSource? _cts = new();

    public MailService(ILogger<MailService> logger, IDatabaseService databaseService)
    {
        _logger = logger;
        _config = databaseService.GetMailServerConfig();
        _databaseService = databaseService;
    }
    
    public void StartSendingLoop()
    {
        if (_sendingTask != null) return;
        _sendingTask = Task.Run(SendingLoop);
    }
    
    public void AddToQueue(FilePair pair)
    {
        if (pair is { ReconNumber: > 0, Timestamp.Year: > 2000 }) 
        {
            _queue.Enqueue(pair);
        }
    }
    
    private async Task SendingLoop()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            if (_queue.IsEmpty)
            {
                await Task.Delay(1000); 
                continue;
            }

            // 1. Беремо перший файл
            if (_queue.TryDequeue(out var firstFile))
            {
                var miniBatch = new List<FilePair> { firstFile };
                int currentReconId = firstFile.ReconNumber;

                // 2. Пробуємо "підхопити" другий файл, якщо він для того ж об'єкта
                // (Щоб відправити парою, якщо вони йшли підряд)
                if (_queue.TryPeek(out var nextFile) && nextFile.ReconNumber == currentReconId)
                {
                    if (_queue.TryDequeue(out var secondFile))
                    {
                        miniBatch.Add(secondFile);
                    }
                }

                // 3. Відправляємо цей міні-батч (1 або 2 файли)
                try 
                {
                    // Отримуємо отримувачів для цього ID
                    var recipients = await _databaseService.GetRecipientsByReconIdAsync(currentReconId);
                    
                    if (recipients != null && recipients.Any())
                    {
                        await SendEmailForGroupAsync(currentReconId, recipients, miniBatch);
                        _logger.LogInformation($"Відправлено {miniBatch.Count} файлів для об'єкта {currentReconId}");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Помилка відправки пошти для {currentReconId}");
                }

                // 4. Пауза між листами, щоб не заблокували SMTP (анти-спам)
                await Task.Delay(1000); 
            }
        }
    }

    public async Task SendToAllAsync(IEnumerable<string> recipients, string subject, string body, IEnumerable<string>? attachments = null)
    {
        
        if (string.IsNullOrEmpty(_config.SmtpServer))
        {
            _logger.LogError("Немає налаштувань SMTP сервера!");
            return; 
        }
        const int batchSize = 50;
        
        var bodyBuilder = new BodyBuilder { TextBody = body };
        AddAttachments(bodyBuilder, attachments);
        var messageBody = bodyBuilder.ToMessageBody();
        
        try
        {
            using (var client = new SmtpClient())
            {
                client.CheckCertificateRevocation = false;
                
                await client.ConnectAsync(_config.SmtpServer, _config.Port, 
                    _config.UseSsl ? SecureSocketOptions.Auto : SecureSocketOptions.None);
                
                if (!string.IsNullOrEmpty(_config.AuthLogin))
                {
                    await client.AuthenticateAsync(_config.AuthLogin, _config.AuthPassword);
                }

                foreach (string[] batch in recipients.Chunk(batchSize))
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
                            {
                                message.Bcc.Add(address);
                            }
                            else
                            {
                                _logger.LogWarning("Некоректний email: {Email}", email);
                            }
                        }
                        
                        if (message.Bcc.Count > 0)
                        {
                            await client.SendAsync(message);
                            _logger.LogInformation("Пачка листів ({Count} шт) відправлена.", message.Bcc.Count);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError("Помилка відправки на {Email}: {Msg}", batch, ex.Message);
                    }
                }
                
                await client.DisconnectAsync(true);
            }
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

    private void AddAttachments(BodyBuilder bodyBuilder, IEnumerable<string>? attachments)
    {
        if (attachments == null) return;

        foreach (var path in attachments)
        {
            if (File.Exists(path))
            {
                try
                {
                    bodyBuilder.Attachments.Add(path);
                }
                catch (Exception ex)
                {
                    _logger.LogError("Не вдалося прикріпити файл {Path}: {Msg}", path, ex.Message);
                }
            }
            else
            {
                _logger.LogWarning("Файл для вкладення не знайдено: {Path}", path);
            }
        }
    }

    public async Task ProcessBatchAsync(List<FilePair> batch)
    {
        if (batch == null || !batch.Any()) return;

        //_logger.LogInformation($"[Mail] Початок обробки розсилки для {batch.Count} записів.");
        
        var groupedFiles = batch
            .Where(f => f.ReconNumber > 0) 
            .GroupBy(f => f.ReconNumber);

        foreach (var group in groupedFiles)
        {
            int reconId = group.Key;
            var filesInGroup = group.ToList();
            var recipients = await _databaseService.GetRecipientsByReconIdAsync(reconId);
            
            if (recipients == null || !recipients.Any())
            {
                _logger.LogWarning($"[Mail] Не знайдено отримувачів для об'єкта ID={reconId}. Пропуск.");
                continue;
            }
            
            var freshFiles = filesInGroup
                .Where(f => f.Timestamp > DateTime.Now.AddDays(-3))
                .ToList();

            if (!freshFiles.Any()) continue;

            try 
            {
                await SendEmailForGroupAsync(reconId, recipients, freshFiles);
                
                await Task.Delay(1000); 
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[Mail] Помилка відправки пошти для об'єкта ID={reconId}");
            }
        }
    }
    
    private async Task SendEmailForGroupAsync(int reconId, List<string> recipients, List<FilePair> files)
    {
        if (!files.Any()) return;
        if (string.IsNullOrEmpty(_config.SmtpServer))
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
        
        var pathsToSend = new List<string>();
        foreach (var pair in files)
        {
            if (pair.Express != null && !string.IsNullOrEmpty(pair.Express.FullPath)) 
                pathsToSend.Add(pair.Express.FullPath);

            if (pair.Data != null && !string.IsNullOrEmpty(pair.Data.FullPath)) 
                pathsToSend.Add(pair.Data.FullPath);
        }

        AddAttachments(bodyBuilder, pathsToSend);
        
        int recipientsBatchSize = 50;
        
        var messageBody = bodyBuilder.ToMessageBody();
        try
        {
            using (var client = new SmtpClient())
            {
                client.CheckCertificateRevocation = false;
                
                await client.ConnectAsync(_config.SmtpServer, _config.Port, 
                    _config.UseSsl ? SecureSocketOptions.Auto : SecureSocketOptions.None);
                
                if (!string.IsNullOrEmpty(_config.AuthLogin))
                {
                    await client.AuthenticateAsync(_config.AuthLogin, _config.AuthPassword);
                }

                foreach (string[] userBatch in recipients.Chunk(recipientsBatchSize))
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
                            {
                                message.Bcc.Add(address);
                            }
                            else
                            {
                                _logger.LogWarning("Некорректный email: {Email}", email);
                            }
                        }
                        
                        if (message.Bcc.Count == 0) continue;
                        
                        try
                        {
                            await client.SendAsync(message);
                            var recipientsString = string.Join(", ", message.Bcc);
                            _logger.LogInformation("ПУспішно відправлено пакет для: {Emails}", recipientsString);
                        }
                        
                        catch (SmtpCommandException ex)
                        {
                            // Обработка случая, когда один из адресов не существует или отклонен сервером
                            if (ex.ErrorCode == SmtpErrorCode.RecipientNotAccepted || ex.ErrorCode == SmtpErrorCode.MessageNotAccepted)
                            {
                                _logger.LogWarning($"Помилка пакетної відправки ({ex.Message}). Пробуємо поштучну відправку...");

                                // ФОЛБЕК: Пробуем отправить каждому отдельно, чтобы найти "битый" адрес
                                foreach (var internetAddress in message.Bcc)
                                {
                                    if (internetAddress is not MailboxAddress recipient) 
                                    {
                                        continue; 
                                    }
                                    
                                    try
                                    {
                                        var singleMessage = new MimeMessage();
                                        singleMessage.From.Add(new MailboxAddress(_config.NameSender, _config.EmailSender));
                                        singleMessage.To.Add(recipient); 
                                        singleMessage.Subject = subject;
                                        singleMessage.Body = messageBody;

                                        await client.SendAsync(singleMessage);
                                    }
                                    catch (SmtpCommandException singleEx)
                                    {
                                        // Вот тут мы ловим конкретного несуществующего пользователя
                                        _logger.LogError($"Не вдалося відправити на {recipient.Address}. Причина: {singleEx.Message} (Code: {singleEx.ErrorCode})");
                                    }
                                    catch (Exception ex2)
                                    {
                                        _logger.LogError($"Загальна помилка при відправці на {recipient.Address}: {ex2.Message}");
                                    }
                                }
                            }
                            else
                            {
                                // Другая ошибка SMTP (например, спам-фильтр или размер файла)
                                _logger.LogError($"SMTP помилка групи: {ex.Message}");
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError($"Загальна помилка відправки групи: {ex.Message}");
                        }
                    }
                    catch (Exception ex)
                    {
                        string emailsStr = string.Join(", ", userBatch);
                        _logger.LogError("Помилка відправки групи листів. Адреси: [{Emails}]. Помилка: {Msg}", emailsStr, ex.Message);                    
                    }
                }
                
                await client.DisconnectAsync(true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Критична помилка SMTP підключення для об'єкта {Obj}", objectName);        
        }
    }
    
    public void Stop()
    {
        _cts.Cancel();
    }
}    
