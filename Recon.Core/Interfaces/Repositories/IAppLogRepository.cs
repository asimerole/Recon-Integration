using Recon.Core.Models;

namespace Recon.Core.Interfaces.Repositories;

public interface IAppLogRepository
{
    Task LogAsync(int serviceId, string actionType, string? details = null, int? userId = null);
    Task<List<AppLogEntry>> GetLogsAsync(int limit = 20000);
    Task CleanupOldLogsAsync(int keepCount = 20000);
}
