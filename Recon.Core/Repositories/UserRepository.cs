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

    public User? GetUserByLogin(string username)
    {
        try
        {
            using var connection = _db.Create();
            return connection.QuerySingleOrDefault<User>(
                "SELECT login AS Username, password AS PasswordHash FROM users WHERE login = @Login",
                new { Login = username });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при поиске пользователя {Login}", username);
            throw;
        }
    }

    public List<string> GetActiveUserEmails()
    {
        try
        {
            using var connection = _db.Create();
            return connection.Query<string>(
                "SELECT login FROM users WHERE status = @Status",
                new { Status = UserStatus.Active }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при сборе почт активных пользователей");
            return new List<string>();
        }
    }

    public List<string> GetAllUserEmails()
    {
        try
        {
            using var connection = _db.Create();
            return connection.Query<string>("SELECT login FROM users").ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при сборе почт всех пользователей");
            return new List<string>();
        }
    }

    public Dictionary<string, List<string>> GetUsersGroupedBySubstation()
    {
        var result = new Dictionary<string, List<string>>();

        const string sql = @"
            SELECT LEFT(us.[login], 255) AS login, LEFT(un.[substation], 255) AS substation
            FROM [users] us
            JOIN [users_units] uu ON us.[id] = uu.[user_id]
            JOIN [units] un ON uu.[unit_id] = un.[id]
            WHERE us.[status] = 1 AND us.[send_mail] = 1";

        try
        {
            using var connection = _db.Create();
            using var reader = connection.ExecuteReader(sql);

            while (reader.Read())
            {
                string login = reader["login"]?.ToString() ?? string.Empty;
                string substation = reader["substation"]?.ToString() ?? string.Empty;

                if (!result.ContainsKey(substation))
                    result[substation] = new List<string>();

                result[substation].Add(login);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Помилка при групуванні користувачів по підстанціям");
        }

        return result;
    }
}
