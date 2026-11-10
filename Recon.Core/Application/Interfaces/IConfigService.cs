using Recon.Core.Application.Dtos;

namespace Recon.Core.Application.Interfaces;

public interface IConfigService
{
    DbConnectionParamsDto LoadDatabaseConfig(string configFilePath);
}
