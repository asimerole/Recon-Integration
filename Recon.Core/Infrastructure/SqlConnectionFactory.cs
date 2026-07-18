using Microsoft.Data.SqlClient;

namespace Recon.Core.Infrastructure;

public class SqlConnectionFactory : IDbConnectionFactory
{
    private string? _connectionString;

    public bool IsInitialized => !string.IsNullOrEmpty(_connectionString);

    public void SetConnectionString(string connectionString) =>
        _connectionString = connectionString;

    public SqlConnection Create()
    {
        if (!IsInitialized)
            throw new InvalidOperationException("Підключення до БД не ініціалізовано. Спочатку виконайте вхід.");
        return new SqlConnection(_connectionString);
    }
}
