using Recon.Core.Domain.Models;

namespace Recon.Core.Application.Interfaces;

public interface IMailService
{
    // True while the "Поштова розсилка" module is switched on
    bool IsRunning { get; }

    void StartSendingLoop();
    void StopSendingLoop();

    /// <summary>Puts an integrated RECON/REXPR event into mail_outbox. Ignored while the module is off.</summary>
    Task AddToQueueAsync(FilePair pair);

    // DAILY file of one registrator, to the users bound to that object
    Task SendDailyFileAsync(ServerInfo server, string filePath);

    // Mass mailing ("Повідомити всіх", morning report) — sent immediately, not through the outbox
    Task<MailSendResult> SendToAllAsync(IEnumerable<string> recipients, string subject, string body, IEnumerable<string>? attachments = null);
}

/// <param name="Sent">recipients the server accepted</param>
/// <param name="Rejected">addresses the server refused (with its reason)</param>
/// <param name="Invalid">logins that are not e-mail addresses</param>
/// <param name="Error">connection / protocol error; null if the message went out</param>
public record MailSendResult(int Sent, IReadOnlyList<string> Rejected, IReadOnlyList<string> Invalid, string? Error)
{
    public bool Success => Error is null && Sent > 0;
}
