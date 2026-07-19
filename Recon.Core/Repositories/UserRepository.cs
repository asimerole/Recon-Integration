using Dapper;
using Microsoft.Extensions.Logging;
using Recon.Core.Enums;
using Recon.Core.Infrastructure;
using Recon.Core.Interfaces.Repositories;
using Recon.Core.Models;

namespace Recon.Core.Repositories;

public class UserRepository : IUserRepository
{
    private readonly IDbConnectionFactory _db;
    private readonly ILogger<UserRepository> _logger;

    public UserRepository(IDbConnectionFactory db, ILogger<UserRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<User?> GetUserByLoginAsync(string username)
    {
        const string sql = @"
            SELECT login AS Username, password AS PasswordHash
            FROM users
            WHERE login = @LoginParam";
        try
        {
            using var conn = await _db.BuildAndOpenConnectionAsync();
            return await conn.QuerySingleOrDefaultAsync<User>(sql, new { LoginParam = username });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Помилка пошуку користувача {Login}", username);
            throw;
        }
    }

    public async Task<List<string>> GetActiveUserEmailsAsync()
    {
        const string sql = "SELECT login FROM users WHERE status = @Status";
        try
        {
            using var conn = await _db.BuildAndOpenConnectionAsync();
            var result = await conn.QueryAsync<string>(sql, new { Status = UserStatus.Active });
            return result.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Помилка отримання email активних користувачів");
            return [];
        }
    }

    public async Task<List<string>> GetAllUserEmailsAsync()
    {
        const string sql = "SELECT login FROM users";
        try
        {
            using var conn = await _db.BuildAndOpenConnectionAsync();
            var result = await conn.QueryAsync<string>(sql);
            return result.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Помилка отримання email всіх користувачів");
            return [];
        }
    }

    public async Task<Dictionary<string, List<string>>> GetUsersGroupedBySubstationAsync()
    {
        const string sql = @"
            SELECT LEFT(us.login, 255) AS login, LEFT(un.substation, 255) AS substation
            FROM users us
            JOIN users_units uu ON us.id = uu.user_id
            JOIN units un ON uu.unit_id = un.id
            WHERE us.status = 1 AND us.send_mail = 1";

        var result = new Dictionary<string, List<string>>();
        try
        {
            using var conn = await _db.BuildAndOpenConnectionAsync();
            var rows = await conn.QueryAsync(sql);
            foreach (var row in rows)
            {
                string login = row.login?.ToString() ?? "";
                string substation = row.substation?.ToString() ?? "";
                if (!result.ContainsKey(substation))
                    result[substation] = [];
                result[substation].Add(login);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Помилка отримання користувачів по підстанціях");
        }
        return result;
    }
}
