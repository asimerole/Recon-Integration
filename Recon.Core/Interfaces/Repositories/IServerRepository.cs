using Recon.Core.Models;

namespace Recon.Core.Interfaces.Repositories;

public interface IServerRepository
{
    Task<List<ServerInfo>> GetAllServersAsync();
    Task UpdateServerStatusAsync(int structId, DateTime? lastPing = null, DateTime? lastRecon = null, DateTime? lastDaily = null);
    Task UpdateDailyStatAsync(int serverId, string columnName);
    Task<List<DailyReportRow>> GetDailyReportAsync(DateTime? date = null);
    Task<bool> TryMarkDailySentAsync(int structId);
}
