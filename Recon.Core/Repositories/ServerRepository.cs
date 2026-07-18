using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Recon.Core.Enums;
using Recon.Core.Infrastructure;
using Recon.Core.Interfaces.Repositories;
using Recon.Core.Models;

namespace Recon.Core.Repositories;

public class ServerRepository : IServerRepository
{
    private readonly IDbConnectionFactory _db;
    private readonly ILogger<ServerRepository> _logger;

    private static readonly string[] AllowedStatColumns = ["collected", "emailed", "integrated", "uploaded"];

    public ServerRepository(IDbConnectionFactory db, ILogger<ServerRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<List<ServerInfo>> GetAllServersAsync()
    {
        const string sql = @"
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
            FROM units u
            JOIN struct_units su ON u.id = su.unit_id
            JOIN struct s ON su.struct_id = s.id
            JOIN FTP_servers fs ON fs.unit_id = u.id
            JOIN FTP_Directories d ON d.struct_id = s.id
            WHERE fs.status = @ServerStatus AND d.isActiveDir = @DirStatus";
        try
        {
            using var conn = _db.Create();
            var servers = (await conn.QueryAsync<ServerInfo>(sql, new
            {
                ServerStatus = ServerStatus.Active,
                DirStatus = DirStatus.Active
            })).ToList();

            foreach (var server in servers)
            {
                if (server.IsFourDigits && !string.IsNullOrEmpty(server.RemoteFolderPath)
                    && server.RemoteFolderPath.Length < 256)
                {
                    server.RemoteFolderPath = server.RemoteFolderPath.Insert(1, "1");
                }
            }
            return servers;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Помилка отримання списку серверів");
            throw;
        }
    }

    public async Task UpdateServerStatusAsync(int structId, DateTime? lastPing = null,
        DateTime? lastRecon = null, DateTime? lastDaily = null)
    {
        const string sql = @"
            MERGE INTO logs AS target
            USING (SELECT @StructId AS struct_id) AS source
            ON (target.struct_id = source.struct_id)
            WHEN MATCHED THEN
                UPDATE SET
                    last_ping  = ISNULL(@LastPing,  target.last_ping),
                    last_recon = ISNULL(@LastRecon, target.last_recon),
                    last_daily = ISNULL(@LastDaily, target.last_daily)
            WHEN NOT MATCHED THEN
                INSERT (struct_id, last_ping, last_recon, last_daily)
                VALUES (@StructId, @LastPing, @LastRecon, @LastDaily);";
        try
        {
            using var conn = _db.Create();
            await conn.ExecuteAsync(sql, new { StructId = structId, LastPing = lastPing, LastRecon = lastRecon, LastDaily = lastDaily });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Помилка оновлення логів для StructId: {Id}", structId);
        }
    }

    public async Task UpdateDailyStatAsync(int serverId, string columnName)
    {
        if (serverId == 0) return;
        if (!AllowedStatColumns.Contains(columnName))
        {
            _logger.LogError("UpdateDailyStatAsync: невідома колонка '{Col}'", columnName);
            return;
        }

        string updateSql = $@"
            UPDATE server_daily_stats
            SET {columnName} = {columnName} + 1
            WHERE server_id = @Id AND stat_date = CAST(GETDATE() AS DATE)";

        string insertSql = $@"
            INSERT INTO server_daily_stats (server_id, stat_date, {columnName})
            VALUES (@Id, CAST(GETDATE() AS DATE), 1)";
        try
        {
            using var conn = _db.Create();
            int rows = await conn.ExecuteAsync(updateSql, new { Id = serverId });
            if (rows == 0)
            {
                try { await conn.ExecuteAsync(insertSql, new { Id = serverId }); }
                catch (SqlException ex) when (ex.Number == 2627)
                {
                    await conn.ExecuteAsync(updateSql, new { Id = serverId });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Помилка оновлення статистики для serverId: {Id}", serverId);
        }
    }
}
