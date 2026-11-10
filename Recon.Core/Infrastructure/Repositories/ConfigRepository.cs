using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Dapper;
using Microsoft.Extensions.Logging;
using Recon.Core.Infrastructure.Db;
using Recon.Core.Application.Interfaces.Repositories;
using Recon.Core.Application.Options;

namespace Recon.Core.Infrastructure.Repositories;

public class ConfigRepository : IConfigRepository
{
    private readonly IDbConnectionFactory _db;
    private readonly ILogger<ConfigRepository> _logger;

    // internal: tests deserialize real setting rows with exactly these options
    internal static readonly JsonSerializerOptions JsonOptions = new()
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
        GetSettingAsync<ModuleConfig>("file_integration", new ModuleConfig());

    public async Task SaveModuleConfigAsync(ModuleConfig config)
    {
        const string sql = "UPDATE [access_settings] SET [value] = @Json WHERE [name] = 'file_integration'";
        try
        {
            using var conn = await _db.BuildAndOpenConnectionAsync();

            // Merge into the stored JSON: Recon-Properties keeps its own flags there
            // (restrictions, mailIntegrationIsActive) which this app must not wipe
            var row = await conn.QuerySingleOrDefaultAsync(
                "sp_GetAccessSettingByName",
                new { Name = "file_integration" },
                commandType: System.Data.CommandType.StoredProcedure);
            var json = TryParseObject((string?)row?.value);

            json["integrationIsActive"] = config.IsIntegrationActive;
            json["ftpIsActive"]         = config.IsFtpActive;
            json["mailIsActive"]        = config.IsMailActive;
            json["oneDriveIsActive"]    = config.IsOneDriveActive;
            json["dbIsFull"]            = config.DbIsFull;
            json["fastDbBuild"]         = config.FastDatabaseBuild;

            await conn.ExecuteAsync(sql, new { Json = json.ToJsonString() });
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

    private static JsonObject TryParseObject(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new JsonObject();
        try
        {
            return JsonNode.Parse(raw) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    private async Task<T?> GetSettingAsync<T>(string settingName, T? defaultValue = default)
    {
        try
        {
            using var conn = await _db.BuildAndOpenConnectionAsync();
            var row = await conn.QuerySingleOrDefaultAsync(
                "sp_GetAccessSettingByName",
                new { Name = settingName },
                commandType: System.Data.CommandType.StoredProcedure);
            string? raw = row?.value;

            if (string.IsNullOrEmpty(raw))
            {
                _logger.LogWarning("Налаштування '{Setting}' не знайдено в БД або порожнє", settingName);
                return defaultValue;
            }

            if (typeof(T) == typeof(string))
                return (T)(object)raw;

            if (typeof(T).IsPrimitive || typeof(T) == typeof(decimal))
                return (T)Convert.ChangeType(raw, typeof(T));

            return JsonSerializer.Deserialize<T>(raw, JsonOptions) ?? defaultValue;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Помилка отримання налаштування '{Setting}'", settingName);
            return defaultValue;
        }
    }
}
