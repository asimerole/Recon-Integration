using Recon.Core.Options;

namespace Recon.Core.Interfaces.Repositories;

public interface IConfigRepository
{
    ModuleConfig GetModuleConfig();
    void SaveModuleConfig(ModuleConfig config);
    MailServerConfig GetMailServerConfig();
    OneDriveConfig GetOneDriveConfig();
    string GetRootFolder();
    string GetWinrecPath();
    int GetFeedingTime();
    Task<AzureConfig> GetAzureConfigAsync();
}
