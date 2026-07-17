using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Microsoft.Extensions.Logging;
using Recon.Core.Infrastructure;
using Recon.Core.Interfaces.Repositories;
using Recon.Core.Options;

namespace Recon.Core.Repositories;

public class ConfigRepository : IConfigRepository
{
    private readonly IDbConnectionFactory _db;
    private readonly ILogger<ConfigRepository> _logger;

    public ConfigRepository(IDbConnectionFactory db, ILogger<ConfigRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    public ModuleConfig GetModuleConfig() =>
        GetSetting<ModuleConfig>("file_integration") ?? new ModuleConfig();

    public void SaveModuleConfig(ModuleConfig config)
    {
        try
        {
            using var connection = _db.Create();
            string json = JsonSerializer.Serialize(config);
            connection.Execute(
                "UPDATE [access_settings] SET [value] = @Json WHERE [name] = 'file_integration'",
                new { Json = json });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Помилка збереження конфігурації модулів");
        }
    }

    public MailServerConfig GetMailServerConfig() =>
        GetSetting<MailServerConfig>("mail") ?? new MailServerConfig();

    public OneDriveConfig GetOneDriveConfig() =>
        GetSetting<OneDriveConfig>("onedrive") ?? new OneDriveConfig();

    public string GetRootFolder() =>
        GetSetting<string>("root_directory") ?? string.Empty;

    public string GetWinrecPath() =>
        GetSetting<string>("winrec-bs") ?? string.Empty;

    public int GetFeedingTime() =>
        GetSetting<int>("feeding_cycle");

    public async Task<AzureConfig> GetAzureConfigAsync()
    {
        using var conn = _db.Create();
        string? json = await conn.QueryFirstOrDefaultAsync<string>(
            "SELECT [value] FROM [access_settings] WHERE [name] = @Name",
            new { Name = "onedrive" });

        if (string.IsNullOrEmpty(json))
            return null!;

        try
        {
            return JsonSerializer.Deserialize<AzureConfig>(json)!;
        }
        catch (JsonException ex)
        {
            throw new Exception($"Помилка парсингу Azure конфігурації: {ex.Message}");
        }
    }

    private T? GetSetting<T>(string settingName)
    {
        try
        {
            using var connection = _db.Create();
            string? rawValue = connection.QuerySingleOrDefault<string>(
                "SELECT value FROM access_settings WHERE name = @Name",
                new { Name = settingName });

            if (string.IsNullOrEmpty(rawValue))
            {
                _logger.LogWarning("Налаштування {Setting} не знайдено в БД або воно пусте.", settingName);
                return default;
            }

            if (typeof(T) == typeof(string))
                return (T)(object)rawValue;

            if (typeof(T).IsPrimitive || typeof(T) == typeof(decimal))
                return (T)Convert.ChangeType(rawValue, typeof(T));

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                NumberHandling = JsonNumberHandling.AllowReadingFromString
            };
            return JsonSerializer.Deserialize<T>(rawValue, options);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при получении настройки {Setting}.", settingName);
            throw;
        }
    }
}
