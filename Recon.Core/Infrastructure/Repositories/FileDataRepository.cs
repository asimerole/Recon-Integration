using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Recon.Core.Infrastructure.Db;
using Recon.Core.Application.Interfaces.Repositories;
using Recon.Core.Domain.Models;

namespace Recon.Core.Infrastructure.Repositories;

public class FileDataRepository : IFileDataRepository
{
    private const int BatchCommandTimeoutSeconds = 300;

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
            using var conn = await _db.BuildAndOpenConnectionAsync();
            await conn.ExecuteAsync(
                "[dbo].[sp_RebuildDatabase]",
                commandType: CommandType.StoredProcedure,
                commandTimeout: 600);
            _logger.LogInformation("База даних успішно перезбудована");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Помилка перезбудови бази даних");
            throw;
        }
    }

    public async Task<string?> GetTargetFolderByReconIdAsync(int reconId)
    {
        const string sql = "SELECT TOP 1 [files_path] FROM [struct] WHERE recon_id = @ReconId";
        using var conn = await _db.BuildAndOpenConnectionAsync();
        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@ReconId", reconId);
        var result = await cmd.ExecuteScalarAsync();
        return result?.ToString();
    }

    public async Task<List<string>> GetRecipientsByReconIdAsync(int reconId, string? objectName, bool restricted)
    {
        using var conn = await _db.BuildAndOpenConnectionAsync();
        var result = await conn.QueryAsync<string>(
            "sp_GetMailRecipientsByReconId",
            new { ReconId = reconId, ObjectName = objectName, Restricted = restricted },
            commandType: System.Data.CommandType.StoredProcedure);
        return result.ToList();
    }

    public async Task<List<string>> GetObjectFoldersAsync()
    {
        using var conn = await _db.BuildAndOpenConnectionAsync();
        var result = await conn.QueryAsync<string>(
            "sp_GetObjectFolders",
            commandType: System.Data.CommandType.StoredProcedure);
        return result.ToList();
    }

    public async Task<int> EnsureStructureExistsAsync(string unitName, string substationName,
        string objectName, int reconNumber, string objectFolderPath)
    {
        const string sql = @"
            -- The folder is the identity of an object. A renamed unit used to create a second struct
            -- for the same folder, and every file of it was then written into data twice.
            DECLARE @StructId INT = (SELECT TOP 1 id FROM struct WHERE files_path = @ObjectPath ORDER BY id);
            IF @StructId IS NOT NULL
            BEGIN
                SELECT @StructId;
                RETURN;
            END

            BEGIN TRANSACTION;

            DECLARE @UnitId INT;

            SELECT @UnitId = id FROM units WHERE unit = @UnitName AND substation = @SubstationName;
            IF @UnitId IS NULL
            BEGIN
                INSERT INTO units (unit, substation) VALUES (@UnitName, @SubstationName);
                SET @UnitId = SCOPE_IDENTITY();
            END

            SELECT @StructId = s.id
            FROM struct s
            JOIN struct_units su ON s.id = su.struct_id
            WHERE s.recon_id = @ReconNum AND s.object = @ObjectName AND su.unit_id = @UnitId;

            IF @StructId IS NULL
            BEGIN
                INSERT INTO struct (recon_id, object, files_path) VALUES (@ReconNum, @ObjectName, @ObjectPath);
                SET @StructId = CAST(SCOPE_IDENTITY() AS INT);
            END
            ELSE
            BEGIN
                UPDATE struct SET files_path = @ObjectPath WHERE id = @StructId;
            END

            IF NOT EXISTS (SELECT 1 FROM struct_units WHERE unit_id = @UnitId AND struct_id = @StructId)
            BEGIN
                INSERT INTO struct_units (unit_id, struct_id) VALUES (@UnitId, @StructId);
            END

            COMMIT TRANSACTION;

            SELECT @StructId;";
        try
        {
            using var conn = await _db.BuildAndOpenConnectionAsync();
            return await conn.ExecuteScalarAsync<int>(sql, new
            {
                UnitName = unitName, SubstationName = substationName,
                ObjectName = objectName, ReconNum = reconNumber, ObjectPath = objectFolderPath
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Помилка заповнення структури БД");
            return 0;
        }
    }

    public async Task<BatchInsertResult> InsertBatchAsync(IReadOnlyList<FilePair> batch)
    {
        if (batch.Count == 0) return new BatchInsertResult(0, 0, []);

        var dataTable = BuildImportTable(batch);

        using var conn = await _db.BuildAndOpenConnectionAsync();
        using var tx = conn.BeginTransaction();
        try
        {
            await conn.ExecuteAsync(SqlCreateTempTable, transaction: tx);

            using (var bulkCopy = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default, tx))
            {
                bulkCopy.DestinationTableName = "#ImportBuffer";
                bulkCopy.BulkCopyTimeout = BatchCommandTimeoutSeconds;
                foreach (DataColumn column in dataTable.Columns)
                    bulkCopy.ColumnMappings.Add(column.ColumnName, column.ColumnName);
                await bulkCopy.WriteToServerAsync(dataTable);
            }

            using var results = await conn.QueryMultipleAsync(SqlUpsert, transaction: tx,
                commandTimeout: BatchCommandTimeoutSeconds);
            var orphans = (await results.ReadAsync<(int ReconNum, string Object, string FileNum)>())
                .Select(o => $"{o.ReconNum}/{o.Object}/{o.FileNum}")
                .ToList();
            int updated = await results.ReadSingleAsync<int>();
            int inserted = await results.ReadSingleAsync<int>();

            await tx.CommitAsync();
            return new BatchInsertResult(inserted, updated, orphans);
        }
        catch (Exception ex)
        {
            try { await tx.RollbackAsync(); } catch { /* connection may be broken already */ }
            _logger.LogError(ex, "Помилка вставки батчу");
            throw;
        }
    }

    internal static DataTable BuildImportTable(IReadOnlyList<FilePair> batch)
    {
        var table = new DataTable();
        table.Columns.Add("StructId", typeof(int));
        table.Columns.Add("ReconNum", typeof(int));
        table.Columns.Add("FileNum", typeof(string));
        table.Columns.Add("Object", typeof(string));
        table.Columns.Add("Date", typeof(DateTime));
        table.Columns.Add("Time", typeof(TimeSpan));
        table.Columns.Add("EventAt", typeof(DateTime));
        table.Columns.Add("DataBinary", typeof(byte[]));
        table.Columns.Add("ExpressBinary", typeof(byte[]));
        table.Columns.Add("OtherBinary", typeof(byte[]));
        table.Columns.Add("FileType", typeof(string));
        table.Columns.Add("Family", typeof(string));
        table.Columns.Add("HasExpress", typeof(bool));
        table.Columns.Add("HasParsedTime", typeof(bool));
        table.Columns.Add("DataSize", typeof(long));
        table.Columns.Add("OtherSize", typeof(long));
        table.Columns.Add("DamagedLine", typeof(string));
        table.Columns.Add("Factor", typeof(string));
        table.Columns.Add("TypeKz", typeof(string));

        foreach (var pair in batch)
        {
            var main = (BaseFile?)pair.Express ?? (BaseFile?)pair.Data ?? pair.Other;
            if (main == null) continue;

            DateTime ts = pair.Timestamp;
            if (ts.Year < 2000) continue;   // no usable time — would land as 0001-01-01

            var row = table.NewRow();
            row["StructId"] = pair.StructId > 0 ? pair.StructId : DBNull.Value;
            row["ReconNum"] = main.ReconNumber;
            row["FileNum"] = main.FileNum;
            row["Object"] = main.Object;
            row["Date"] = ts.Date;
            row["Time"] = ts.TimeOfDay;
            row["EventAt"] = ts;

            row["DataBinary"] = (object?)pair.Data?.BinaryData ?? DBNull.Value;
            row["ExpressBinary"] = (object?)pair.Express?.BinaryData ?? DBNull.Value;
            row["OtherBinary"] = (object?)pair.Other?.BinaryData ?? DBNull.Value;
            row["FileType"] = pair.FileType;
            // RECON and REXPR of one event are the same record whatever arrives first
            row["Family"] = pair.FileType is "RECON" or "REXPR" ? "RECON" : pair.FileType;

            row["HasExpress"] = pair.Express?.BinaryData != null;
            row["HasParsedTime"] = pair.Express is { HasParsedTimestamp: true };
            // Sizes tell "the same file delivered twice" from "another event with a reset number"
            row["DataSize"] = (object?)pair.Data?.BinaryData?.LongLength ?? DBNull.Value;
            row["OtherSize"] = (object?)pair.Other?.BinaryData?.LongLength ?? DBNull.Value;
            row["DamagedLine"] = (object?)pair.Express?.DamagedLine ?? DBNull.Value;
            row["Factor"] = (object?)pair.Express?.Factor ?? DBNull.Value;
            row["TypeKz"] = (object?)pair.Express?.TypeKz ?? DBNull.Value;

            table.Rows.Add(row);
        }
        return table;
    }

    private const string SqlCreateTempTable = @"
        CREATE TABLE #ImportBuffer (
            StructId       INT            NULL,
            ReconNum       INT,
            FileNum        VARCHAR(30)    COLLATE DATABASE_DEFAULT,
            [Object]       NVARCHAR(255)  COLLATE DATABASE_DEFAULT,
            [Date]         DATE,
            [Time]         TIME(3),
            EventAt        DATETIME2(3),
            DataBinary     VARBINARY(MAX),
            ExpressBinary  VARBINARY(MAX),
            OtherBinary    VARBINARY(MAX),
            FileType       VARCHAR(15)    COLLATE DATABASE_DEFAULT,
            Family         VARCHAR(15)    COLLATE DATABASE_DEFAULT,
            HasExpress     BIT,
            HasParsedTime  BIT,
            DataSize       BIGINT         NULL,
            OtherSize      BIGINT         NULL,
            DamagedLine    NVARCHAR(255)  COLLATE DATABASE_DEFAULT,
            Factor         NVARCHAR(MAX)  COLLATE DATABASE_DEFAULT,
            TypeKz         NVARCHAR(255)  COLLATE DATABASE_DEFAULT
        );";

    // Result sets: 1) orphans, 2) rows updated, 3) rows inserted
    private const string SqlUpsert = @"
        SET NOCOUNT ON;

        -- 1. Object: from .meta / object folder; otherwise ONE struct with this recon number and name.
        --    The old JOIN matched every such struct and wrote the file into each of them.
        UPDATE t SET StructId = s.id
        FROM #ImportBuffer t
        CROSS APPLY (SELECT TOP 1 s.id FROM struct s
                     WHERE s.recon_id = t.ReconNum AND s.[object] = t.[Object] COLLATE DATABASE_DEFAULT
                     ORDER BY s.id) s
        WHERE t.StructId IS NULL;

        SELECT ReconNum, [Object], FileNum FROM #ImportBuffer WHERE StructId IS NULL;

        -- 2. Exact repeats inside the batch collapse to the most complete row, then each row looks for its
        --    record in data. The old key (object, number, date) split one event into two records when RECON
        --    (dated by its file mtime) and REXPR (dated by the event) fell on different days.
        --    Registrators RESET numbering (new year, reboot), so the same number a day later can be another
        --    event — closeness alone is not enough (checked on the customer DB, see Database/diag_twins.sql):
        --      a) both have REXPR: same event only with the same REXPR time (±2 s);
        --      b) only one has REXPR (event time E), the other only RECON (file mtime T): T is from 1 h before
        --         to 49 h after E (files are collected later; ±1 h clock/DST); two RECONs must be the same size;
        --      c) neither has REXPR (lone RECON, DAILY, RNET_…): same file delivered twice, ±65 min, same size.
        WITH ranked AS (
            SELECT *, ROW_NUMBER() OVER (
                       PARTITION BY StructId, Family, FileNum, EventAt
                       ORDER BY HasExpress DESC, HasParsedTime DESC) AS rn
            FROM #ImportBuffer
            WHERE StructId IS NOT NULL
        )
        SELECT r.*, m.id AS ExistingId
        INTO #Resolved
        FROM ranked r
        OUTER APPLY (
            SELECT TOP 1 d.id
            FROM data d
            CROSS APPLY (SELECT DATEADD(MILLISECOND,
                                        DATEDIFF(MILLISECOND, CAST('00:00' AS TIME), ISNULL(d.[time], CAST('00:00' AS TIME))),
                                        CAST(d.[date] AS DATETIME2(3))) AS At,
                                CASE WHEN d.express_file IS NULL THEN 0 ELSE 1 END AS HasRexpr,
                                DATALENGTH(d.data_file) AS ReconSize,
                                DATALENGTH(d.other_type_file) AS OtherSize) dt
            WHERE d.struct_id = r.StructId
              AND d.file_num = r.FileNum COLLATE DATABASE_DEFAULT
              AND d.[date] BETWEEN DATEADD(DAY, -3, r.[Date]) AND DATEADD(DAY, 3, r.[Date])
              AND (   (r.Family = 'RECON' AND d.file_type IN ('RECON', 'REXPR'))
                   OR d.file_type = r.FileType COLLATE DATABASE_DEFAULT)
              AND (
                      (r.HasExpress = 1 AND dt.HasRexpr = 1
                       AND ABS(DATEDIFF(MILLISECOND, dt.At, r.EventAt)) <= 2000)
                   OR (r.HasExpress = 1 AND dt.HasRexpr = 0
                       AND DATEDIFF(MINUTE, r.EventAt, dt.At) BETWEEN -65 AND 2940
                       AND (r.DataSize IS NULL OR dt.ReconSize IS NULL OR dt.ReconSize = r.DataSize))
                   OR (r.HasExpress = 0 AND dt.HasRexpr = 1
                       AND DATEDIFF(MINUTE, dt.At, r.EventAt) BETWEEN -65 AND 2940
                       AND (r.DataSize IS NULL OR dt.ReconSize IS NULL OR dt.ReconSize = r.DataSize))
                   OR (r.HasExpress = 0 AND dt.HasRexpr = 0
                       AND ABS(DATEDIFF(MINUTE, dt.At, r.EventAt)) <= 65
                       AND ISNULL(dt.ReconSize, -1) = ISNULL(r.DataSize, -1)
                       AND ISNULL(dt.OtherSize, -1) = ISNULL(r.OtherSize, -1))
                  )
            ORDER BY ABS(DATEDIFF(SECOND, dt.At, r.EventAt))
        ) m
        WHERE r.rn = 1;

        -- 3. Complete existing records: add the missing file, take the exact event time from REXPR
        UPDATE d SET
            data_file       = COALESCE(r.DataBinary,    d.data_file),
            express_file    = COALESCE(r.ExpressBinary, d.express_file),
            other_type_file = COALESCE(r.OtherBinary,   d.other_type_file),
            [date]          = CASE WHEN r.HasParsedTime = 1 THEN r.[Date] ELSE d.[date] END,
            [time]          = CASE WHEN r.HasParsedTime = 1 THEN r.[Time] ELSE d.[time] END,
            file_type       = CASE WHEN r.Family = 'RECON' AND COALESCE(r.DataBinary, d.data_file) IS NOT NULL
                                   THEN 'RECON' ELSE r.FileType END
        FROM data d
        JOIN #Resolved r ON r.ExistingId = d.id;

        SELECT @@ROWCOUNT AS Updated;

        -- 4. New events
        CREATE TABLE #Inserted (id INT, StructId INT, FileNum VARCHAR(30) COLLATE DATABASE_DEFAULT,
                                [Date] DATE, FileType VARCHAR(15) COLLATE DATABASE_DEFAULT);

        INSERT INTO data (struct_id, [date], [time], file_num, data_file, express_file, other_type_file, file_type)
        OUTPUT INSERTED.id, INSERTED.struct_id, INSERTED.file_num, INSERTED.[date], INSERTED.file_type
        INTO #Inserted (id, StructId, FileNum, [Date], FileType)
        SELECT StructId, [Date], [Time], FileNum, DataBinary, ExpressBinary, OtherBinary, FileType
        FROM #Resolved
        WHERE ExistingId IS NULL;

        SELECT @@ROWCOUNT AS Inserted;

        UPDATE r SET ExistingId = i.id
        FROM #Resolved r
        JOIN #Inserted i ON i.StructId = r.StructId AND i.FileNum = r.FileNum
                        AND i.[Date] = r.[Date] AND i.FileType = r.FileType
        WHERE r.ExistingId IS NULL;

        -- 5. Parsed REXPR fields
        UPDATE dp SET damaged_line = r.DamagedLine, [trigger] = r.Factor, event_type = r.TypeKz
        FROM data_process dp
        JOIN #Resolved r ON dp.id = r.ExistingId
        WHERE r.HasExpress = 1;

        INSERT INTO data_process (id, damaged_line, [trigger], event_type)
        SELECT r.ExistingId, r.DamagedLine, r.Factor, r.TypeKz
        FROM #Resolved r
        WHERE r.HasExpress = 1 AND r.ExistingId IS NOT NULL
          AND NOT EXISTS (SELECT 1 FROM data_process dp WHERE dp.id = r.ExistingId);

        DROP TABLE #Inserted;
        DROP TABLE #Resolved;
        DROP TABLE #ImportBuffer;";
}
