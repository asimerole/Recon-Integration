using Recon.Core.Models;

namespace Recon.Core.Interfaces;

public interface IMailService
{
    // Sending one letter
    Task SendEmailAsync(string toEmail, string subject, string body, IEnumerable<string>? attachments = null);
    
    // Mass mailing (for the “Notify everyone” function)
    Task SendToAllAsync(IEnumerable<string> recipients, string subject, string body, IEnumerable<string>? attachments = null);

    Task ProcessBatchAsync(List<FilePair> batch);

    void AddToQueue(FilePair pair);

    void StartSendingLoop();
}