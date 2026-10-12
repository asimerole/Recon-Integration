using Dapper;
using Recon.Core.Infrastructure;
using Recon.Core.Interfaces.Repositories;
using Recon.Core.Models;

namespace Recon.Core.Repositories;

public class AppLogRepository : IAppLogRepository
{
    private readonly IDbConnectionFactory _db;

    public AppLogRepository(IDbConnectionFactory db)
    {
        _db = db;
    }

    public async Task LogAsync(int serviceId, string actionType, string? details = null, int? userId = null)
    {
        try
        {
            using var conn = await _db.BuildAndOpenConnectionAsync();
            await conn.ExecuteAsync(
                "sp_InsertAppLog",
                new { ServiceId = serviceId, UserId = userId, ActionType = actionType, Details = details },
                commandType: System.Data.CommandType.StoredProcedure);
        }
        catch
        {
            // Логгер не повинен кидати виключення
        }
    }

    public async Task<List<AppLogEntry>> GetLogsAsync(int limit = 20000)
    {
        try
        {
            using var conn = await _db.BuildAndOpenConnectionAsync();
            var result = await conn.QueryAsync<AppLogEntry>(@"
                SELECT TOP (@Limit) Id, ServiceId, ActionType, Details, CreatedAt
                FROM AppLogs
                ORDER BY CreatedAt DESC",
                new { Limit = limit });
            return result.ToList();
        }
        catch { return new List<AppLogEntry>(); }
    }

    public Task CleanupOldLogsAsync(int keepCount = 20000)
        => Task.CompletedTask; // чистка вбудована в sp_InsertAppLog
}
