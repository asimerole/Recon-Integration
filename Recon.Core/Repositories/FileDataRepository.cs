using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Recon.Core.Infrastructure;
using Recon.Core.Interfaces.Repositories;
using Recon.Core.Models;

namespace Recon.Core.Repositories;

public class FileDataRepository : IFileDataRepository
{
    private readonly IDbConnectionFactory _db;
    private readonly ILogger<FileDataRepository> _logger;

    public FileDataRepository(IDbConnectionFactory db, ILogger<FileDataRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task RebuildDatabaseAsync()
    {
        try
        {
            using var connection = _db.Create();
            await connection.ExecuteAsync(
                "[dbo].[sp_RebuildDatabase]",
                commandType: CommandType.StoredProcedure,
                commandTimeout: 600);
            _logger.LogInformation("Database rebuilt successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error rebuilding database");
            throw;
        }
    }

    public async Task<string?> GetTargetFolderByReconIdAsync(int reconId)
    {
        using var connection = _db.Create();
        await connection.OpenAsync();
        using var cmd = new SqlCommand("SELECT TOP 1 [files_path] FROM [struct] WHERE recon_id = @ReconId", connection);
        cmd.Parameters.AddWithValue("@ReconId", reconId);
        var result = await cmd.ExecuteScalarAsync();
        return result?.ToString();
    }

    public async Task EnsureStructureExistsAsync(string unitName, string substationName, string objectName, int reconNumber, string objectFolderPath)
    {
        try
        {
            using var connection = _db.Create();
            const string sql = @"
                BEGIN TRANSACTION;

                DECLARE @UnitId INT;
                DECLARE @StructId INT;

                SELECT @UnitId = id FROM [units]
                WHERE [unit] = @UnitName AND [substation] = @SubstationName;

                IF @UnitId IS NULL
                BEGIN
                    INSERT INTO [units] ([unit], [substation]) VALUES (@UnitName, @SubstationName);
                    SET @UnitId = SCOPE_IDENTITY();
                END

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
                    UPDATE [struct] SET [files_path] = @ObjectPath WHERE [id] = @StructId;
                END

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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при заполнении структуры в базе. (units, struct tables)");
        }
    }

    public async Task<List<string>> GetRecipientsByReconIdAsync(int reconId)
    {
        const string sql = @"
            SELECT DISTINCT u.login
            FROM [dbo].[users] u
            INNER JOIN [dbo].[users_units] uu ON u.id = uu.user_id
            INNER JOIN [dbo].[struct_units] su ON uu.unit_id = su.unit_id
            INNER JOIN [dbo].[struct] s ON su.struct_id = s.id
            WHERE s.recon_id = @ReconId
              AND u.send_mail = 1
              AND u.status = 1;";

        using var connection = _db.Create();
        var result = await connection.QueryAsync<string>(sql, new { ReconId = reconId });
        return result.ToList();
    }

    public async Task InsertBatchAsync(List<FilePair> batch)
    {
        if (batch == null || batch.Count == 0) return;

        var dataTable = BuildImportTable(batch);

        using var connection = _db.Create();
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

            const string checkOrphansSql = @"
                SELECT temp.ReconNum, temp.FileNum, temp.Object
                FROM #ImportBuffer temp
                LEFT JOIN [struct] s ON s.recon_id = temp.ReconNum AND s.object = temp.Object
                WHERE s.id IS NULL;";

            using (var cmd = new SqlCommand(checkOrphansSql, connection, transaction))
            using (var reader = await cmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    _logger.LogWarning(
                        "Файл ВІДХИЛЕНО: Об'єкт '{Obj}' (ReconID={ID}) не знайдено в struct. Файл: {Num}.",
                        reader.GetString(2), reader.GetInt32(0), reader.GetString(1));
                }
            }

            await ExecuteCommandAsync(connection, transaction, GetMergeSql());
            await transaction.CommitAsync();
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            _logger.LogError(ex, "Ошибка при вставке батча");
            throw;
        }
    }

    private DataTable BuildImportTable(List<FilePair> batch)
    {
        var table = new DataTable();
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
            var mainInfo = (BaseFile?)pair.Express ?? (BaseFile?)pair.Data ?? pair.Other;
            if (mainInfo == null) continue;

            var row = table.NewRow();
            row["ReconNum"] = mainInfo.ReconNumber;
            row["FileNum"] = mainInfo.FileNum;
            row["Object"] = mainInfo.Object;

            DateTime ts = pair.Express?.Timestamp ?? mainInfo.Timestamp;
            row["Date"] = ts.Date;
            row["Time"] = ts.TimeOfDay;

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

    private static async Task ExecuteCommandAsync(SqlConnection conn, SqlTransaction trans, string sql)
    {
        using var cmd = new SqlCommand(sql, conn, trans);
        await cmd.ExecuteNonQueryAsync();
    }

    private const string SqlCreateTempTable = @"
        CREATE TABLE #ImportBuffer (
            [ReconNum] INT,
            [FileNum] VARCHAR(30) COLLATE DATABASE_DEFAULT,
            [Object] NVARCHAR(255) COLLATE DATABASE_DEFAULT,
            [Date] DATE,
            [Time] TIME(3),
            [DataBinary] VARBINARY(MAX),
            [ExpressBinary] VARBINARY(MAX),
            [OtherBinary] VARBINARY(MAX),
            [FileType] VARCHAR(15) COLLATE DATABASE_DEFAULT,
            [HasExpress] BIT,
            [DamagedLine] NVARCHAR(255) COLLATE DATABASE_DEFAULT,
            [Factor] NVARCHAR(MAX) COLLATE DATABASE_DEFAULT,
            [TypeKz] NVARCHAR(255) COLLATE DATABASE_DEFAULT
        );";

    private static string GetMergeSql() => @"
        MERGE INTO [data] AS target
        USING (
            SELECT * FROM (
                SELECT
                    temp.*,
                    s.id as StructId,
                    ROW_NUMBER() OVER (
                        PARTITION BY s.id, temp.FileNum, temp.Date
                        ORDER BY temp.Time DESC
                    ) as RowNum
                FROM #ImportBuffer temp
                INNER JOIN [struct] s
                    ON s.recon_id = temp.ReconNum
                    AND s.object = temp.Object COLLATE DATABASE_DEFAULT
            ) AS t
            WHERE t.RowNum = 1
        ) AS source
        ON (target.struct_id = source.StructId
            AND target.file_num = source.FileNum COLLATE DATABASE_DEFAULT
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

        MERGE INTO [data_process] AS target
        USING (
            SELECT DISTINCT
                d.id as DataId,
                temp.DamagedLine,
                temp.Factor,
                temp.TypeKz
            FROM #ImportBuffer temp
            INNER JOIN [struct] s
                ON s.recon_id = temp.ReconNum
                AND s.object = temp.Object COLLATE DATABASE_DEFAULT
            INNER JOIN [data] d
                ON d.struct_id = s.id
                AND d.file_num = temp.FileNum COLLATE DATABASE_DEFAULT
                AND d.date = temp.Date
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

        DROP TABLE #ImportBuffer;";
}
