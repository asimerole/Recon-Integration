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
            throw new InvalidOperationException("Database not initialized. Login first.");
        return new SqlConnection(_connectionString);
    }
}
