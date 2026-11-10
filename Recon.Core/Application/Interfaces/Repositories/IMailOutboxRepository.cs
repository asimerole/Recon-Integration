using Recon.Core.Domain.Models;

namespace Recon.Core.Application.Interfaces.Repositories;

public interface IMailOutboxRepository
{
    /// <returns>false if the same files of the object were already queued recently</returns>
    Task<bool> EnqueueAsync(int reconId, string objectName, string? substation, DateTime eventTime, IEnumerable<string> filePaths);

    Task<List<MailOutboxItem>> GetDueAsync(TimeSpan settle, TimeSpan maxWait);

    Task MarkAsync(long id, MailOutboxStatus status, string? recipients = null, string? error = null, TimeSpan? retryAfter = null);

    Task CleanupAsync(int keepDays);
}
