using Dapper;
using Microsoft.Extensions.Logging;
using Recon.Core.Infrastructure;
using Recon.Core.Interfaces.Repositories;
using Recon.Core.Models;

namespace Recon.Core.Repositories;

public class OneDriveRepository : IOneDriveRepository
{
    private readonly IDbConnectionFactory _db;
    private readonly ILogger<OneDriveRepository> _logger;

    public OneDriveRepository(IDbConnectionFactory db, ILogger<OneDriveRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<List<UserAccessDto>> GetUsersForOneDriveUpdateAsync()
    {
        const string sql = @"
            SELECT
                u.id as UserId,
                u.login as Email,
                s.files_path as FilePath,
                CASE WHEN u.type = 'Адмін' THEN 1 ELSE 0 END as IsAdmin
            FROM users u
            JOIN users_units uu ON u.id = uu.user_id
            JOIN struct_units su ON uu.unit_id = su.unit_id
            JOIN struct s ON su.struct_id = s.id
            WHERE u.isOneDriveActive = 1
              AND u.onedrive_access_granted = 0
              AND s.files_path IS NOT NULL";

        using var conn = await _db.BuildAndOpenConnectionAsync();
        var rawData = await conn.QueryAsync<dynamic>(sql);

        return rawData
            .GroupBy(x => (int)x.UserId)
            .Select(g => new UserAccessDto
            {
                UserId = g.Key,
                Email = g.First().Email,
                IsAdmin = (g.First().IsAdmin == 1),
                FolderPaths = g.Where(x => x.FilePath != null)
                    .Select(x => (string)x.FilePath)
                    .Distinct()
                    .ToList()
            })
            .ToList();
    }

    public async Task<List<UserAccessDto>> GetUsersForOneDriveRemovalAsync()
    {
        const string sql = @"
            SELECT
                u.id as UserId,
                u.login as Email,
                s.files_path as FilePath,
                CASE WHEN u.type = 'Адмін' THEN 1 ELSE 0 END as IsAdmin
            FROM users u
            JOIN users_units uu ON u.id = uu.user_id
            JOIN struct_units su ON uu.unit_id = su.unit_id
            JOIN struct s ON su.struct_id = s.id
            WHERE u.isOneDriveActive = 0
              AND u.onedrive_access_granted = 1
              AND s.files_path IS NOT NULL";

        using var conn = await _db.BuildAndOpenConnectionAsync();
        var rawData = await conn.QueryAsync<dynamic>(sql);

        return rawData
            .GroupBy(x => (int)x.UserId)
            .Select(g => new UserAccessDto
            {
                UserId = g.Key,
                Email = g.First().Email,
                IsAdmin = (g.First().IsAdmin == 1),
                FolderPaths = g.Where(x => x.FilePath != null)
                    .Select(x => (string)x.FilePath)
                    .Distinct()
                    .ToList()
            })
            .ToList();
    }

    public async Task MarkOneDriveAccessGrantedAsync(int userId)
    {
        using var conn = await _db.BuildAndOpenConnectionAsync();
        await conn.ExecuteAsync(
            "UPDATE users SET onedrive_access_granted = 1 WHERE id = @Id",
            new { Id = userId });
    }

    public async Task MarkOneDriveAccessRevokedAsync(int userId)
    {
        using var conn = await _db.BuildAndOpenConnectionAsync();
        await conn.ExecuteAsync(
            "UPDATE users SET onedrive_access_granted = 0 WHERE id = @Id",
            new { Id = userId });
    }
}
