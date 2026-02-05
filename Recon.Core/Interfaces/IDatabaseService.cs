using Recon.Core.Models;
using Recon.Core.Options;

namespace Recon.Core.Interfaces;

public interface IDatabaseService
{
    void Initialize(string dbOptionsConnectionString);
    User? GetUserByLogin(string username);

    Task UpdateServerStatusAsync(int reconId, DateTime? lastPing = null, DateTime? lastRecon = null,
        DateTime? lastDaily = null);
    
    OneDriveConfig GetOneDriveConfig();
    
    ModuleConfig GetModuleConfig();
    
    MailServerConfig GetMailServerConfig();

    int GetFeedingTime();

    string GetRootFolder();

    string GetWinrecPath();
    
    void SaveModuleConfig(ModuleConfig config);

    List<ServerInfo> GetAllServers();

    Task RebuildDatabaseAsync();
    
    Task UpdateDailyStatAsync(int serverID, string column);
    
    List<string> GetActiveUserEmails();

    List<string> GetAllUserEmails();
    
    Task EnsureStructureExistsAsync(string unitName, string substationName, string objectName, int reconNumber, string objectFolderPath);
    
    Task InsertBatchAsync(List<FilePair> globalBatch);

    Task<string?> GetTargetFolderByReconIdAsync(int reconId);
    
    Task<List<string>> GetRecipientsByReconIdAsync(int reconId);
    
    Task<List<UserAccessDto>> GetUsersForOneDriveUpdateAsync();
    
    Task MarkOneDriveAccessGrantedAsync(int userId);

    Task<List<UserAccessDto>> GetUsersForOneDriveRemovalAsync();

    Task MarkOneDriveAccessRevokedAsync(int userId);
    
    Task<AzureConfig> GetAzureConfigAsync();
}