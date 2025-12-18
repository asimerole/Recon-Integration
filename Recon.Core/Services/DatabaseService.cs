using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Microsoft.Data.SqlClient;
using Recon.Core.Interfaces;
using Recon.Core.Models;
using Microsoft.Extensions.Logging;
using Recon.Core.Enums;
using Recon.Core.Options;

namespace Recon.Core.Services;

public class DatabaseService : IDatabaseService
{
    private readonly ILogger<DatabaseService> _logger;
    
    private string _connectionString = string.Empty;

    public DatabaseService(ILogger<DatabaseService> logger)
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

    public List<string> GetActiveUserEmails()
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
                string sql = @"SELECT login FROM users WHERE status = @Status";
                
                var users = connection.Query<string>(sql, new {Status = UserStatus.Active}).ToList();
                
                return users;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при сборе почт всех активных пользователей");
            return new List<string>();
        }
    }
    
    // 1. For prime numbers (Feeding Time)
    public int GetFeedingTime() => 
        GetSetting<int>("feeding_cycle");

    // 2. For simple strings (Paths)
    public string GetRootFolder() => 
        GetSetting<string>("root_directory") ?? string.Empty;
    public string GetWinrecPath() => 
        GetSetting<string>("winrec-bs") ?? string.Empty;
    
    // 3. For complex JSON configurations
    public OneDriveConfig GetOneDriveConfig() => 
        GetSetting<OneDriveConfig>("onedrive") ?? new OneDriveConfig();

    public MailServerConfig GetMailServerConfig() => 
        GetSetting<MailServerConfig>("mail") ?? new MailServerConfig();

    public ModuleConfig GetModuleConfig() => 
        GetSetting<ModuleConfig>("file_integration") ?? new ModuleConfig();
    
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
                WHERE fs.status = @ServerStatusValue AND d.isActiveDir = @DirCollectStatus";
                
                var servers = connection.Query<ServerInfo>(sql, new{ ServerStatusValue = ServerStatus.Active, DirCollectStatus = DirStatus.Active }).ToList();
                
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

    public async Task EnsureStructureExistsAsync(string unitName, string substationName, string objectName, int reconNumber,
        string objectFolderPath)
    {
        try
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                string sql = @"
                BEGIN TRANSACTION;
        
                DECLARE @UnitId INT;
                DECLARE @StructId INT;
        
                -- 1. Work with UNITS
                SELECT @UnitId = id FROM [units] 
                WHERE [unit] = @UnitName AND [substation] = @SubstationName;
        
                IF @UnitId IS NULL
                BEGIN
                    INSERT INTO [units] ([unit], [substation]) VALUES (@UnitName, @SubstationName);
                    SET @UnitId = SCOPE_IDENTITY();
                END
        
                -- 2.Work with STRUCT
                SELECT @StructId = s.id 
                FROM [struct] s
                INNER JOIN [struct_units] su ON s.id = su.struct_id
                WHERE s.recon_id = @ReconNum 
                  AND s.object = @ObjectName
                  AND su.unit_id = @UnitId;
        
                IF @StructId IS NULL
                BEGIN
                    INSERT INTO [struct] ([recon_id], [object], [files_path]) 
                    VALUES (@ReconNum, @ObjectName, @ObjectPath);
                    SET @StructId = CAST(SCOPE_IDENTITY() AS INT);
                END
                ELSE
                BEGIN
                    -- update path
                    UPDATE [struct] SET [files_path] = @ObjectPath WHERE [id] = @StructId;
                END
        
                -- 3. link (STRUCT_UNITS)
                IF NOT EXISTS (SELECT 1 FROM [struct_units] WHERE [unit_id] = @UnitId AND [struct_id] = @StructId)
                BEGIN
                    INSERT INTO [struct_units] ([unit_id], [struct_id]) VALUES (@UnitId, @StructId);
                END
        
                COMMIT TRANSACTION;";
                
                await connection.ExecuteAsync(sql, new 
                { 
                    UnitName = unitName, 
                    SubstationName = substationName, 
                    ObjectName = objectName, 
                    ReconNum = reconNumber, 
                    ObjectPath = objectFolderPath 
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,"Ошибка при заполнении структуры в базе. (units, struct tables)");
        }
    }

    // private methods
    
    private T? GetSetting<T>(string settingName)
    {
        if (string.IsNullOrEmpty(_connectionString))
        {
            _logger.LogError("Попытка запроса к БД без инициализации строки подключения. (GetConfigFromDb)");
            return default;
        }

        try
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                string sql = @"SELECT value FROM access_settings WHERE name = @Name";
                
                string? rawValue = connection.QuerySingleOrDefault<string>(sql, new { Name = settingName });
                
                if (string.IsNullOrEmpty(rawValue)) 
                {
                    _logger.LogWarning("Налаштування {Setting} не знайдено в БД або воно пусте.", settingName);
                    return default;
                }
                
                // --- Type definition ---
                // 1. If T is a string (path to a folder)
                if (typeof(T) == typeof(string))
                {
                    return (T)(object)rawValue; 
                }
                
                // 2. If T is a primitive (int, bool, double)
                if (typeof(T).IsPrimitive || typeof(T) == typeof(decimal))
                {
                    return (T)Convert.ChangeType(rawValue, typeof(T));
                }

                // 3. If T is a complex class (Config), then there is JSON there.
                var options = new JsonSerializerOptions 
                {
                    PropertyNameCaseInsensitive = true, 
                    NumberHandling = JsonNumberHandling.AllowReadingFromString 
                };
                
                return JsonSerializer.Deserialize<T>(rawValue, options);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,"Ошибка при получении конфиг файла.", settingName);
            throw;
        }
    }

    public async Task InsertBatchAsync(List<FilePair> batch)
    {
        if (batch == null || batch.Count == 0) return;
        
        var dataTable = BuildImportTable(batch);
        
        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        using var transaction = connection.BeginTransaction();
        try
        {
            await ExecuteCommandAsync(connection, transaction, SqlCreateTempTable);
            
            using (var bulkCopy = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction))
            {
                bulkCopy.DestinationTableName = "#ImportBuffer";
                await bulkCopy.WriteToServerAsync(dataTable);
            }
            
            string checkOrphansSql = @"
                SELECT temp.ReconNum, temp.FileNum, temp.Object
                FROM #ImportBuffer temp
                LEFT JOIN [ReconDB].[dbo].[struct] s 
                    ON s.recon_id = temp.ReconNum AND s.object = temp.Object
                WHERE s.id IS NULL; 
            ";

            using (var cmdCheck = new SqlCommand(checkOrphansSql, connection, transaction))
            using (var reader = await cmdCheck.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    int reconNum = reader.GetInt32(0);
                    string fileNum = reader.GetString(1);
                    string objName = reader.GetString(2);
                    
                    _logger.LogWarning(
                        "⚠️ Файл ОТКЛОНЕН базой данных: Объект '{Obj}' (ReconID={ID}) не найден в таблице struct. Файл: {Num}.", 
                        objName, reconNum, fileNum);
            
                    // LOGIC CAN BE ADDED HERE:
                    // For example, write this file to the “filesToMoveToQuarantine” list 
                    // so that it can be removed from the object folder after the transaction.
                }
            }
            
            await ExecuteCommandAsync(connection, transaction, GetMergeSql());

            await transaction.CommitAsync();
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            _logger.LogError($"Ошибка при вставке батча: {ex.Message}");
            throw;
        }
    }

    private DataTable BuildImportTable(List<FilePair> batch)
    {
        var table = new DataTable();
        // Defining columns 
        table.Columns.Add("ReconNum", typeof(int));
        table.Columns.Add("FileNum", typeof(string));
        table.Columns.Add("Object", typeof(string));
        table.Columns.Add("Date", typeof(DateTime));
        table.Columns.Add("Time", typeof(TimeSpan));
        table.Columns.Add("DataBinary", typeof(byte[]));
        table.Columns.Add("ExpressBinary", typeof(byte[]));
        table.Columns.Add("OtherBinary", typeof(byte[])); 
        table.Columns.Add("FileType", typeof(string));
        table.Columns.Add("HasExpress", typeof(bool));
        table.Columns.Add("DamagedLine", typeof(string));
        table.Columns.Add("Factor", typeof(string));
        table.Columns.Add("TypeKz", typeof(string));
    
        foreach (var pair in batch)
        {
            // logic for selecting the main file
            var mainInfo = (BaseFile?)pair.Express ?? (BaseFile?)pair.Data ?? pair.Other;
            if (mainInfo == null) continue;
    
            var row = table.NewRow();
            
            row["ReconNum"] = mainInfo.ReconNumber;
            row["FileNum"] = mainInfo.FileNum;
            row["Object"] = mainInfo.Object;
    
            // Important: Date for uniqueness
            DateTime ts = pair.Express?.Timestamp ?? mainInfo.Timestamp;
            row["Date"] = ts.Date; 
            row["Time"] = ts.TimeOfDay;
    
            // Filling with null check
            row["DataBinary"] = (object?)pair.Data?.BinaryData ?? DBNull.Value;
            row["ExpressBinary"] = (object?)pair.Express?.BinaryData ?? DBNull.Value;
            row["OtherBinary"] = (object?)pair.Other?.BinaryData ?? DBNull.Value;
            row["FileType"] = pair.Data?.FilePrefix ?? pair.Express?.FilePrefix ?? pair.Other?.FilePrefix ?? "UNKNOWN";
    
            if (pair.Express != null)
            {
                row["HasExpress"] = true;
                row["DamagedLine"] = (object?)pair.Express.DamagedLine ?? DBNull.Value;
                row["Factor"] = (object?)pair.Express.Factor ?? DBNull.Value;
                row["TypeKz"] = (object?)pair.Express.TypeKz ?? DBNull.Value;
            }
            else
            {
                row["HasExpress"] = false;
                row["DamagedLine"] = DBNull.Value;
                row["Factor"] = DBNull.Value;
                row["TypeKz"] = DBNull.Value;
            }
    
            table.Rows.Add(row);
        }
    
        return table;
    }
    
    private async Task ExecuteCommandAsync(SqlConnection conn, SqlTransaction trans, string sql)
    {
        using var cmd = new SqlCommand(sql, conn, trans);
        await cmd.ExecuteNonQueryAsync();
    }
    
    private const string SqlCreateTempTable = @"
        CREATE TABLE #ImportBuffer (
            [ReconNum] INT,
            [FileNum] VARCHAR(30),
            [Object] NVARCHAR(255),
            [Date] DATE,
            [Time] TIME(3),
            [DataBinary] VARBINARY(MAX),
            [ExpressBinary] VARBINARY(MAX),
            [OtherBinary] VARBINARY(MAX),
            [FileType] VARCHAR(15),
            [HasExpress] BIT,
            [DamagedLine] NVARCHAR(255),
            [Factor] NVARCHAR(MAX),
            [TypeKz] NVARCHAR(255)
        );";
    
private string GetMergeSql()
{
    return @"
        -- 1. MERGE DATA
        MERGE INTO [ReconDB].[dbo].[data] AS target
        USING (
            SELECT * FROM (
                SELECT 
                    temp.*, 
                    s.id as StructId,
                    -- !!! МАГИЯ ТУТ: Нумеруем дубликаты (если пришли одинаковые ID, Date, FileNum)
                    ROW_NUMBER() OVER (
                        PARTITION BY s.id, temp.FileNum, temp.Date 
                        ORDER BY temp.Time DESC -- Берем тот, где время новее (или любое)
                    ) as RowNum
                FROM #ImportBuffer temp
                INNER JOIN [ReconDB].[dbo].[struct] s 
                    ON s.recon_id = temp.ReconNum AND s.object = temp.Object
            ) AS t
            WHERE t.RowNum = 1 -- !!! Оставляем только уникальные записи
        ) AS source
        ON (target.struct_id = source.StructId 
            AND target.file_num = source.FileNum 
            AND target.date = source.Date)

        WHEN MATCHED THEN
            UPDATE SET 
                target.data_file = COALESCE(source.DataBinary, target.data_file),
                target.express_file = COALESCE(source.ExpressBinary, target.express_file),
                target.other_type_file = COALESCE(source.OtherBinary, target.other_type_file),
                target.time = CASE WHEN source.HasExpress = 1 THEN source.Time ELSE target.time END,
                target.file_type = source.FileType

        WHEN NOT MATCHED THEN
            INSERT (struct_id, date, time, file_num, data_file, express_file, other_type_file, file_type)
            VALUES (source.StructId, source.Date, source.Time, source.FileNum, source.DataBinary, source.ExpressBinary, source.OtherBinary, source.FileType);

        -- 2. MERGE DATA_PROCESS
        MERGE INTO [ReconDB].[dbo].[data_process] AS target
        USING (
            SELECT DISTINCT -- Добавил DISTINCT на всякий случай
                d.id as DataId,
                temp.DamagedLine,
                temp.Factor,
                temp.TypeKz
            FROM #ImportBuffer temp
            INNER JOIN [ReconDB].[dbo].[struct] s 
                ON s.recon_id = temp.ReconNum AND s.object = temp.Object
            INNER JOIN [ReconDB].[dbo].[data] d
                ON d.struct_id = s.id AND d.file_num = temp.FileNum AND d.date = temp.Date
            WHERE temp.HasExpress = 1
        ) AS source
        ON (target.id = source.DataId)

        WHEN MATCHED THEN
            UPDATE SET 
                target.damaged_line = source.DamagedLine, 
                target.[trigger] = source.Factor, 
                target.event_type = source.TypeKz

        WHEN NOT MATCHED THEN
            INSERT (id, damaged_line, [trigger], event_type)
            VALUES (source.DataId, source.DamagedLine, source.Factor, source.TypeKz);

        DROP TABLE #ImportBuffer;
    ";
}

    public async Task<string?> GetTargetFolderByReconIdAsync(int reconId)
    {
        string sql = @"
        SELECT TOP 1 d.local_path
        FROM [ReconDB].[dbo].[struct] s 
        JOIN [ReconDB].[dbo].[FTP_Directories] d ON d.struct_id = s.id 
        WHERE s.recon_id = @ReconId";

        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
    
        using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@ReconId", reconId);

        var result = await cmd.ExecuteScalarAsync();
        return result?.ToString(); 
    }
}