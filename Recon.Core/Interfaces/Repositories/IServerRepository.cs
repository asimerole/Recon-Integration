using Recon.Core.Models;

namespace Recon.Core.Interfaces.Repositories;

public interface IServerRepository
{
    Task<List<ServerInfo>> GetAllServersAsync();
    Task UpdateServerStatusAsync(int structId, DateTime? lastPing = null, DateTime? lastRecon = null, DateTime? lastDaily = null);
    Task UpdateDailyStatAsync(int serverId, string columnName);
}
