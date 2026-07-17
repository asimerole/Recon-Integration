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

    public Task<ModuleConfig> GetModuleConfigAsync() =>
        GetSettingAsync<ModuleConfig>("file_integration", new ModuleConfig());

    public async Task SaveModuleConfigAsync(ModuleConfig config)
    {
        try
        {
            using var connection = await _db.BuildAndOpenConnectionAsync();
            string json = JsonSerializer.Serialize(config);
            await connection.ExecuteAsync(
                "UPDATE [access_settings] SET [value] = @Json WHERE [name] = 'file_integration'",
                new { Json = json });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Помилка збереження конфігурації модулів");
        }
    }

    public Task<MailServerConfig> GetMailServerConfigAsync() =>
        GetSettingAsync<MailServerConfig>("mail", new MailServerConfig());

    public Task<OneDriveConfig> GetOneDriveConfigAsync() =>
        GetSettingAsync<OneDriveConfig>("onedrive", new OneDriveConfig());

    public async Task<string> GetRootFolderAsync() =>
        await GetSettingAsync<string>("root_directory", string.Empty) ?? string.Empty;

    public async Task<string> GetWinrecPathAsync() =>
        await GetSettingAsync<string>("winrec-bs", string.Empty) ?? string.Empty;

    public async Task<int> GetFeedingTimeAsync() =>
        await GetSettingAsync<int>("feeding_cycle", 0);

    public async Task<AzureConfig> GetAzureConfigAsync()
    {
        using var conn = await _db.BuildAndOpenConnectionAsync();
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

    private async Task<T?> GetSettingAsync<T>(string settingName, T? defaultValue = default)
    {
        try
        {
            using var connection = await _db.BuildAndOpenConnectionAsync();
            string? rawValue = await connection.QuerySingleOrDefaultAsync<string>(
                "SELECT value FROM access_settings WHERE name = @Name",
                new { Name = settingName });

            if (string.IsNullOrEmpty(rawValue))
            {
                _logger.LogWarning("Налаштування {Setting} не знайдено в БД або воно пусте.", settingName);
                return defaultValue;
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
            return JsonSerializer.Deserialize<T>(rawValue, options) ?? defaultValue;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при получении настройки {Setting}.", settingName);
            throw;
        }
    }
}
