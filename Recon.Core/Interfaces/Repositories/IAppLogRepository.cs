namespace Recon.Core.Interfaces.Repositories;

public interface IAppLogRepository
{
    Task LogAsync(int serviceId, string actionType, string? details = null, int? userId = null);
}
