using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;
using Recon.Core.Interfaces;
using Recon.Core.Options;

namespace Recon.Core.Services;

public class MailService : IMailService
{
    private readonly ILogger<MailService> _logger;
    private readonly MailServerConfig _config;

    public MailService(ILogger<MailService> logger, IDatabaseService databaseService)
    {
        _logger = logger;
        _config = databaseService.GetMailServerConfig();
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
        
}