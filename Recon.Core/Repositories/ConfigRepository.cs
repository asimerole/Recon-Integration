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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public ConfigRepository(IDbConnectionFactory db, ILogger<ConfigRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    public Task<ModuleConfig> GetModuleConfigAsync() =>
        GetSettingAsync<ModuleConfig>("file_integration")
            .ContinueWith(t => t.Result ?? new ModuleConfig());

    public async Task SaveModuleConfigAsync(ModuleConfig config)
    {
        const string sql = "UPDATE [access_settings] SET [value] = @Json WHERE [name] = 'file_integration'";
        try
        {
            using var conn = _db.Create();
            await conn.ExecuteAsync(sql, new { Json = JsonSerializer.Serialize(config) });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Помилка збереження конфігурації модулів");
        }
    }

    public Task<MailServerConfig> GetMailServerConfigAsync() =>
        GetSettingAsync<MailServerConfig>("mail")
            .ContinueWith(t => t.Result ?? new MailServerConfig());

    public Task<OneDriveConfig> GetOneDriveConfigAsync() =>
        GetSettingAsync<OneDriveConfig>("onedrive")
            .ContinueWith(t => t.Result ?? new OneDriveConfig());

    public async Task<AzureConfig?> GetAzureConfigAsync()
    {
        const string sql = "SELECT [value] FROM [access_settings] WHERE [name] = @Name";
        try
        {
            using var conn = _db.Create();
            string? json = await conn.QueryFirstOrDefaultAsync<string>(sql, new { Name = "onedrive" });
            if (string.IsNullOrEmpty(json)) return null;
            return JsonSerializer.Deserialize<AzureConfig>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Помилка отримання Azure конфігурації");
            throw;
        }
    }

    public async Task<string> GetRootFolderAsync() =>
        await GetSettingAsync<string>("root_directory") ?? string.Empty;

    public async Task<string> GetWinrecPathAsync() =>
        await GetSettingAsync<string>("winrec-bs") ?? string.Empty;

    public async Task<int> GetFeedingTimeAsync() =>
        await GetSettingAsync<int>("feeding_cycle");

    private async Task<T?> GetSettingAsync<T>(string settingName)
    {
        const string sql = "SELECT value FROM access_settings WHERE name = @Name";
        try
        {
            using var conn = _db.Create();
            string? raw = await conn.QuerySingleOrDefaultAsync<string>(sql, new { Name = settingName });

            if (string.IsNullOrEmpty(raw))
            {
                _logger.LogWarning("Налаштування '{Setting}' не знайдено в БД або порожнє", settingName);
                return default;
            }

            if (typeof(T) == typeof(string))
                return (T)(object)raw;

            if (typeof(T).IsPrimitive || typeof(T) == typeof(decimal))
                return (T)Convert.ChangeType(raw, typeof(T));

            return JsonSerializer.Deserialize<T>(raw, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Помилка отримання налаштування '{Setting}'", settingName);
            throw;
        }
    }
}
