using Recon.Core.Models;
using Recon.Core.Options;

namespace Recon.Core.Interfaces;

public interface IDatabaseService
{
    void Initialize(string dbOptionsConnectionString);
    User? GetUserByLogin(string username);
    
    string GetRootFolder();

    int GetFeedingTime();

    Task UpdateServerStatusAsync(int reconId, DateTime? lastPing = null, DateTime? lastRecon = null,
        DateTime? lastDaily = null);
    
    OneDriveConfig GetOneDriveConfig();
    
    ModuleConfig GetModuleConfig();
    
    MailServerConfig GetMailServerConfig();
    
    void SaveModuleConfig(ModuleConfig config);

    List<ServerInfo> GetAllServers();

    Task RebuildDatabaseAsync();
    
    Task UpdateDailyStatAsync(int serverID, string column);
}