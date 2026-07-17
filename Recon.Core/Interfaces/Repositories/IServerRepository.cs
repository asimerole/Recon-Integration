using Recon.Core.Models;

namespace Recon.Core.Interfaces.Repositories;

public interface IServerRepository
{
    List<ServerInfo> GetAllServers();
    Task UpdateServerStatusAsync(int structId, DateTime? lastPing = null, DateTime? lastRecon = null, DateTime? lastDaily = null);
    Task UpdateDailyStatAsync(int serverId, string columnName);
}
