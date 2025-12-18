using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Recon.Core.Interfaces;
using Recon.Core.Options;
using System.Text.Json;
using System.Text.RegularExpressions;

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
        
    
        // This pattern searches for: 
        // 1. Any known keyword (server, database, etc.)
        // 2. An equal sign
        // 3. The value until the end of the line (or until a semicolon)
        string pattern = @"(?<key>server|data source|address|database|initial catalog|user id|username|user|uid|password|pwd|port)\s*=\s*(?<value>[^;\r\n]+)";
        var matches = Regex.Matches(json, pattern, RegexOptions.IgnoreCase);

        foreach (Match match in matches)
        {
            string key = match.Groups["key"].Value.ToLowerInvariant();
            string value = match.Groups["value"].Value.Trim().Trim('"', '\''); 

            _logger.LogInformation($"Найдено: {key} = {value}");

            switch (key)
            {
                case "server":
                case "data source":
                case "address":
                    options.Server = value;
                    break;

                case "database":
                case "initial catalog":
                    options.Name = value;
                    break;

                case "username":
                case "user id":
                case "user":
                case "uid":
                    options.Username = value;
                    break;

                case "password":
                case "pwd":
                    options.Password = value;
                    break;

                case "port":
                    if (int.TryParse(value, out int parsedPort)) options.Port = parsedPort;
                    break;
            }
        }
        
        builder.DataSource = options.Port > 0 ? $"{options.Server},{options.Port}" : options.Server;
        
        builder.InitialCatalog = options.Name;
        builder.UserID = options.Username;
        builder.Password = options.Password;
            
        builder.MultiSubnetFailover = true;
        builder.TrustServerCertificate = false;
        builder.Encrypt = false;    
        builder.ConnectTimeout = options.CommandTimeout > 0 ? options.CommandTimeout : 30;
        options.ConnectionString = builder.ConnectionString;
        
        //_logger.LogInformation($"Сформирована строка подключения: DataSource={builder.DataSource}; Catalog={builder.InitialCatalog}; User={builder.UserID}");
        
        return options;
    }
}