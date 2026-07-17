using Recon.Core.Dtos;
using Recon.Core.Options;

namespace Recon.Core.Interfaces;

public interface IConfigService
{
    DbConnectionParamsDto LoadDatabaseConfig(string configFilePath);
}