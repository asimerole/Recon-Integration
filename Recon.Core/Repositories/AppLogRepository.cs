using Dapper;
using Recon.Core.Infrastructure;
using Recon.Core.Interfaces.Repositories;

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
                "INSERT INTO AppLogs (ServiceId, UserId, ActionType, Details, CreatedAt) " +
                "VALUES (@ServiceId, @UserId, @ActionType, @Details, GETDATE())",
                new { ServiceId = serviceId, UserId = userId, ActionType = actionType, Details = details });
        }
        catch
        {
            // Логгер не повинен кидати виключення
        }
    }
}
