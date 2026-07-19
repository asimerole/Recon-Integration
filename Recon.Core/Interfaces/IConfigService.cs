using Recon.Core.Dtos;

namespace Recon.Core.Interfaces;

public interface IConfigService
{
    DbConnectionParamsDto LoadDatabaseConfig(string configFilePath);
}
