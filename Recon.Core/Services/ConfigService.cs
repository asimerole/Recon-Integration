using Microsoft.Extensions.Logging;
using Recon.Core.Dtos;
using Recon.Core.Interfaces;
using System.Text.Json;

namespace Recon.Core.Services;

public class ConfigService : IConfigService
{
    private readonly ICryptoService _cryptoService;
    private readonly ILogger<ConfigService> _logger;
        
    public ConfigService(ICryptoService cryptoService, ILogger<ConfigService> logger)
    {
        _cryptoService = cryptoService;
        _logger = logger;
    }

    public DbConnectionParamsDto LoadDatabaseConfig(string configFilePath)
    {
        string json = _cryptoService.DecryptConfig(configFilePath);
        return JsonSerializer.Deserialize<DbConnectionParamsDto>(json) ?? new DbConnectionParamsDto();
    }
}
