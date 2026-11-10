using Recon.Core.Domain.Models;

namespace Recon.Core.Application.Interfaces.Repositories;

public interface IAppLogRepository
{
    Task LogAsync(int serviceId, string actionType, string? details = null, int? userId = null);
    Task<List<AppLogEntry>> GetLogsAsync(int limit = 20000);
    Task CleanupOldLogsAsync(int keepCount = 20000);
}
