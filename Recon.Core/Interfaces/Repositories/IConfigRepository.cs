using Recon.Core.Options;

namespace Recon.Core.Interfaces.Repositories;

public interface IConfigRepository
{
    Task<ModuleConfig> GetModuleConfigAsync();
    Task SaveModuleConfigAsync(ModuleConfig config);
    Task<MailServerConfig> GetMailServerConfigAsync();
    Task<OneDriveConfig> GetOneDriveConfigAsync();
    Task<string> GetRootFolderAsync();
    Task<string> GetWinrecPathAsync();
    Task<int> GetFeedingTimeAsync();
    Task<AzureConfig> GetAzureConfigAsync();
}
