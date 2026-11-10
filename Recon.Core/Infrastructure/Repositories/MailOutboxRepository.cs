using System.Data;
using Dapper;
using Recon.Core.Application.Interfaces.Repositories;
using Recon.Core.Domain.Models;
using Recon.Core.Infrastructure.Db;

namespace Recon.Core.Infrastructure.Repositories;

public class MailOutboxRepository : IMailOutboxRepository
{
    private readonly IDbConnectionFactory _db;

    public MailOutboxRepository(IDbConnectionFactory db)
    {
        _db = db;
    }

    public async Task<bool> EnqueueAsync(int reconId, string objectName, string? substation, DateTime eventTime, IEnumerable<string> filePaths)
    {
        using var conn = await _db.BuildAndOpenConnectionAsync();
        int queued = await conn.ExecuteScalarAsync<int>(
            "sp_MailOutbox_Enqueue",
            new
            {
                ReconId = reconId,
                Object = objectName,
                Substation = substation,
                EventTime = eventTime,
                FilePaths = string.Join(MailOutboxItem.PathSeparator, filePaths)
            },
            commandType: CommandType.StoredProcedure);
        return queued == 1;
    }

    public async Task<List<MailOutboxItem>> GetDueAsync(TimeSpan settle, TimeSpan maxWait)
    {
        using var conn = await _db.BuildAndOpenConnectionAsync();
        var rows = await conn.QueryAsync<MailOutboxItem>(
            "sp_MailOutbox_GetDue",
            new { SettleSeconds = (int)settle.TotalSeconds, MaxWaitSeconds = (int)maxWait.TotalSeconds },
            commandType: CommandType.StoredProcedure);
        return rows.ToList();
    }

    public async Task MarkAsync(long id, MailOutboxStatus status, string? recipients = null, string? error = null, TimeSpan? retryAfter = null)
    {
        using var conn = await _db.BuildAndOpenConnectionAsync();
        await conn.ExecuteAsync(
            "sp_MailOutbox_Mark",
            new
            {
                Id = id,
                Status = (byte)status,
                Recipients = recipients,
                Error = error is { Length: > 1000 } ? error[..1000] : error,
                RetryAfterSeconds = retryAfter.HasValue ? (int?)retryAfter.Value.TotalSeconds : null
            },
            commandType: CommandType.StoredProcedure);
    }

    public async Task CleanupAsync(int keepDays)
    {
        using var conn = await _db.BuildAndOpenConnectionAsync();
        await conn.ExecuteAsync("sp_MailOutbox_Cleanup", new { KeepDays = keepDays },
            commandType: CommandType.StoredProcedure);
    }
}
