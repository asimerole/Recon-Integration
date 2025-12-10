using Recon.Core.Options;

namespace Recon.Core.Interfaces;

public interface IConfigService
{
    DatabaseOptions LoadDatabaseConfig(string configFilePath);
}