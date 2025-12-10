using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using Recon.Core.Interfaces;
using Recon.Core.Models;
using Microsoft.Extensions.Logging;
using Recon.Core.Options;

namespace Recon.Core.Services;

public class DatabaseService : IDatabaseService
{
    private readonly ILogger<FtpService> _logger;
    
    private string _connectionString = string.Empty;
    
    public string ConnectionString => _connectionString;

    public DatabaseService(ILogger<FtpService> logger)
    {
        _logger = logger;
    }
    public void Initialize(string dbOptionsConnectionString)
    {
        _logger.LogInformation("Initializing database service");
        _connectionString = dbOptionsConnectionString;
    }

    public User? GetUserByLogin(string username)
    {
        if (string.IsNullOrEmpty(_connectionString))
        {
            _logger.LogError("Попытка запроса к БД без инициализации строки подключения. (GetUserByLogin)");
            return null;
        }

        try
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                string sql = @"
                SELECT 
                    login AS Username, 
                    password AS PasswordHash 
                FROM users 
                WHERE login = @LoginParam";
                
                var user = connection.QuerySingleOrDefault<User>(sql, new { LoginParam = username });
                
                return user;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при поиске пользователя {Login}", username);
            throw;
        }
    }

    public string GetRootFolder()
    {
        if (string.IsNullOrEmpty(_connectionString))
        {
            _logger.LogError("Попытка запроса к БД без инициализации строки подключения. (GetRootFolder)");
            return "";
        }
        
        try
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                string sql = @"SELECT value FROM access_settings WHERE name = 'root_directory'";
                
                var result = connection.QuerySingleOrDefault<string>(sql);
                
                return result!;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,"Ошибка при получении корневой папки из базы.");
            throw;
        }
    }

    public int GetFeedingTime()
    {
        if (string.IsNullOrEmpty(_connectionString))
        {
            _logger.LogError("Попытка запроса к БД без инициализации строки подключения. (GetFeedingTime)");
            return 0;
        }
        
        try
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                string sql = @"SELECT value FROM access_settings WHERE name = 'feeding_cycle'";
                
                var result = connection.QuerySingleOrDefault<int>(sql);
                
                return result!;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,"Ошибка при получении времени паузы ФТП.");
            throw;
        }
        return 0;
    }
    
    public OneDriveConfig GetOneDriveConfig() => 
        GetConfigFromDb<OneDriveConfig>("onedrive");

    public MailServerConfig GetMailServerConfig() => 
        GetConfigFromDb<MailServerConfig>("mail");

    public ModuleConfig GetModuleConfig() => 
        GetConfigFromDb<ModuleConfig>("file_integration");

    private T GetConfigFromDb<T>(string settingName) where T : new()
    {
        if (string.IsNullOrEmpty(_connectionString))
        {
            _logger.LogError("Попытка запроса к БД без инициализации строки подключения. (GetConfigFromDb)");
            return new T();
        }

        try
        {
            using (var conection = new SqlConnection(_connectionString))
            {
                string sql = @"SELECT value FROM access_settings WHERE name = @Name";
                
                string? jsonString = conection.QuerySingleOrDefault<string>(sql, new { Name = settingName });
                
                if (string.IsNullOrEmpty(jsonString)) 
                {
                    _logger.LogWarning("Налаштування {Setting} не знайдено в БД або воно пусте.", settingName);
                    return new T();
                }

                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var config = JsonSerializer.Deserialize<T>(jsonString, options);
                
                return config?? new T();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,"Ошибка при получении конфиг файла.", settingName);
            throw;
        }
    }
    
    public void SaveModuleConfig(ModuleConfig config)
    {
        if (string.IsNullOrEmpty(_connectionString)) return;
    
        try 
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                string jsonString = JsonSerializer.Serialize(config);
                
                string sql = "UPDATE [access_settings] SET [value] = @Json WHERE [name] = 'file_integration'";
                
                connection.Execute(sql, new { Json = jsonString });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Помилка збереження конфігурації модулів");
        }
    }
    
    public List<ServerInfo> GetAllServers()
    {
        if (string.IsNullOrEmpty(_connectionString))
        {
            _logger.LogError("Попытка запроса к БД без инициализации строки подключения. (GetUserByLogin)");
            return null!;
        }

        try
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                string sql = @"
                SELECT 
                    fs.id AS Id,
                    u.unit AS Unit, 
                    u.substation AS Substation, 
                    s.object AS Object, 
                    fs.IP_addr AS IpAddress,      
                    fs.login AS Login, 
                    fs.password AS Password, 
                    fs.status AS Status,        
                    s.recon_id AS ReconId,
                    s.id AS StructId,
                    d.remote_path AS RemoteFolderPath, 
                    d.local_path AS LocalFolderPath,
                    d.IsFourDigits AS IsFourDigits
                FROM [units] u 
                JOIN [struct_units] su ON u.id = su.unit_id 
                JOIN [struct] s ON su.struct_id = s.id 
                JOIN [FTP_servers] fs ON fs.unit_id = u.id 
                JOIN [FTP_Directories] d ON d.struct_id = s.id 
                WHERE fs.status = 1 AND d.isActiveDir = 1";
                
                var servers = connection.Query<ServerInfo>(sql).ToList();
                
                foreach (var server in servers)
                {
                    if (server.IsFourDigits && !string.IsNullOrEmpty(server.RemoteFolderPath))
                    {
                        if (server.RemoteFolderPath.Length < 256) 
                        {
                            server.RemoteFolderPath = server.RemoteFolderPath.Insert(1, "1");
                        }
                    }
                }
                
                return servers!;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка во время получения списка серверов");
            throw;
        }
    }
    
    public async Task RebuildDatabaseAsync()
    {
        if (string.IsNullOrEmpty(_connectionString)) 
        {
            _logger.LogError("RebuildDatabaseAsync: Connection string is empty");
            return;
        }

        try 
        {
            using (var connection = new SqlConnection(_connectionString))
            {
               
                int timeoutSeconds = 300; 

                string sql = @"
                BEGIN TRANSACTION;
                DELETE FROM [data];
                DELETE FROM [struct_units];
                DELETE FROM [struct];
                DELETE FROM [units];          
                DBCC CHECKIDENT ('[units]', RESEED, 0);
                DBCC CHECKIDENT ('[struct]', RESEED, 0);
                DBCC CHECKIDENT ('[data]', RESEED, 0);
                COMMIT;";
            
                
                await connection.ExecuteAsync(sql, commandTimeout: timeoutSeconds);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Помилка при очищенні бази даних");
            throw; 
        }
    }
    
    public async Task UpdateServerStatusAsync(int structId, DateTime? lastPing = null, DateTime? lastRecon = null, DateTime? lastDaily = null)
    {
        if (string.IsNullOrEmpty(_connectionString)) 
        {
            _logger.LogError("UpdateServerStatusAsync: Connection string is empty");
            return;
        }
        
        try
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                string sql = @"
                MERGE INTO [logs] AS target
                USING (SELECT @StructId AS struct_id) AS source
                ON (target.struct_id = source.struct_id)
                WHEN MATCHED THEN
                    UPDATE SET 
                        last_ping = ISNULL(@LastPing, target.last_ping),
                        last_recon = ISNULL(@LastRecon, target.last_recon),
                        last_daily = ISNULL(@LastDaily, target.last_daily)
                WHEN NOT MATCHED THEN
                    INSERT (struct_id, last_ping, last_recon, last_daily)
                    VALUES (@StructId, @LastPing, @LastRecon, @LastDaily);";
                
                await connection.ExecuteAsync(sql, new 
                { 
                    StructId = structId,
                    LastPing = lastPing, 
                    LastRecon = lastRecon, 
                    LastDaily = lastDaily 
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Помилка оновлення логів для StructId: {Id}", structId);
        }
    }

    public async Task UpdateDailyStatAsync(int serverId, string columnName)
    {
        var allowedColumns = new[] { "collected", "emailed", "integrated", "uploaded" };
        if (!allowedColumns.Contains(columnName))
        {
            _logger.LogError("UpdateDailyStatAsync: Невідома колонка '{Col}'", columnName);
            return;
        }
        
        if (string.IsNullOrEmpty(_connectionString)) 
        {
            _logger.LogError("UpdateDailyStatAsync: Connection string is empty");
            return;
        }

        try
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                string updateSql = $@"
                UPDATE server_daily_stats 
                SET {columnName} = {columnName} + 1 
                WHERE server_id = @Id AND stat_date = CAST(GETDATE() AS DATE)";

                int rowsAffected = await connection.ExecuteAsync(updateSql, new { Id = serverId });
                
                if (rowsAffected == 0)
                {
                    string insertSql = $@"
                    INSERT INTO server_daily_stats (server_id, stat_date, {columnName}) 
                    VALUES (@Id, CAST(GETDATE() AS DATE), 1)";

                    try 
                    {
                        await connection.ExecuteAsync(insertSql, new { Id = serverId });
                    }
                    catch (SqlException ex) when (ex.Number == 2627) 
                    {
                        await connection.ExecuteAsync(updateSql, new { Id = serverId });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Помилка оновлення логів для serverID: {Id}", serverId);
        }
    }
    
}