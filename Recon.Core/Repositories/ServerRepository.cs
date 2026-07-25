using System.Data;
using Dapper;
using Microsoft.Extensions.Logging;
using Recon.Core.Infrastructure;
using Recon.Core.Interfaces.Repositories;
using Recon.Core.Models;

namespace Recon.Core.Repositories;

public class ServerRepository : IServerRepository
{
    private readonly IDbConnectionFactory _db;
    private readonly ILogger<ServerRepository> _logger;

    public ServerRepository(IDbConnectionFactory db, ILogger<ServerRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<List<ServerInfo>> GetAllServersAsync()
    {
        try
        {
            using var conn = await _db.BuildAndOpenConnectionAsync();
            var servers = (await conn.QueryAsync<ServerInfo>(
                "sp_GetActiveFtpServers",
                commandType: CommandType.StoredProcedure)).ToList();

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
        try
        {
            using var conn = await _db.BuildAndOpenConnectionAsync();
            await conn.ExecuteAsync(
                "sp_UpdateServerStatus",
                new { StructId = structId, LastPing = lastPing, LastRecon = lastRecon, LastDaily = lastDaily },
                commandType: CommandType.StoredProcedure);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Помилка оновлення логів для StructId: {Id}", structId);
        }
    }

    public async Task UpdateDailyStatAsync(int structId, string statType)
    {
        if (structId == 0) return;
        try
        {
            using var conn = await _db.BuildAndOpenConnectionAsync();
            await conn.ExecuteAsync(
                "sp_UpdateDailyStat",
                new { StructId = structId, StatType = statType },
                commandType: CommandType.StoredProcedure);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Помилка оновлення статистики для structId: {Id}", structId);
        }
    }

    public async Task<bool> TryMarkDailySentAsync(int structId)
    {
        try
        {
            using var conn = await _db.BuildAndOpenConnectionAsync();
            var result = await conn.ExecuteScalarAsync<int>(
                "sp_TryMarkDailySent",
                new { StructId = structId },
                commandType: CommandType.StoredProcedure);
            return result == 1;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Помилка TryMarkDailySent для structId: {Id}", structId);
            return false;
        }
    }

    public async Task<List<DailyReportRow>> GetDailyReportAsync(DateTime? date = null)
    {
        try
        {
            using var conn = await _db.BuildAndOpenConnectionAsync();
            var rows = await conn.QueryAsync<DailyReportRow>(
                "sp_GetDailyReport",
                new { ReportDate = date?.Date },
                commandType: CommandType.StoredProcedure);
            return rows.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Помилка отримання суточного звіту");
            return new List<DailyReportRow>();
        }
    }
}
