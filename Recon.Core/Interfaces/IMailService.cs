using Recon.Core.Models;

namespace Recon.Core.Interfaces;

public interface IMailService
{
    // Sending one letter
    Task SendEmailAsync(string toEmail, string subject, string body, IEnumerable<string>? attachments = null);
    
    // Mass mailing (for the “Notify everyone” function)
    Task SendToAllAsync(IEnumerable<string> recipients, string subject, string body, IEnumerable<string>? attachments = null);

    void AddToQueue(FilePair pair);

    // DAILY file of one registrator, to the users bound to that object
    Task SendDailyFileAsync(ServerInfo server, string filePath);

    // True while the "Поштова розсилка" module is switched on
    bool IsRunning { get; }

    void StartSendingLoop();
    void StopSendingLoop();
}