using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Recon.Core.Interfaces;
using Recon.Core.Options;
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

    public DatabaseOptions LoadDatabaseConfig(string configFilePath)
    {
        string json = _cryptoService.DecryptConfig(configFilePath);
        return ParseDatabaseConfig(json);
    }

    private DatabaseOptions ParseDatabaseConfig(string json)
    {
        var options = new DatabaseOptions();
        var builder = new SqlConnectionStringBuilder();
        
        var lines = json.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            if (!line.Contains("=")) continue;

            var parts = line.Split('=', 2);
            if (parts.Length != 2) continue;

            var key = parts[0].Trim();
            var value = parts[1].Trim();
            
            switch (key) 
            {
                case "server": options.Server = value; break;
                case "database": options.Name = value; break;
                case "username": options.Username = value; break;
                case "password": options.Password = value; break;
                case "port": 
                    if (int.TryParse(value, out int parsedPort))
                    {
                        options.Port = parsedPort;
                    }
                    else 
                    {
                        _logger.LogWarning($"Warning: Invalid port value '{value}'");
                    }
                    break;
            }
        }
        
        builder.DataSource = options.Port > 0 ? $"{options.Server},{options.Port}" : options.Server;
        
        builder.DataSource = options.Server;
        builder.InitialCatalog = options.Name;
        builder.UserID = options.Username;
        builder.Password = options.Password;
            
        builder.MultiSubnetFailover = true;
        builder.TrustServerCertificate = true;
        builder.Encrypt = false;    
        builder.ConnectTimeout = options.CommandTimeout;
        options.ConnectionString = builder.ConnectionString;
        
        return options;
    }
}