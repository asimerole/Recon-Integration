using System.Data;
using Microsoft.Data.SqlClient;
using Recon.Core.Dtos;

namespace Recon.Core.Infrastructure;

public class SqlConnectionFactory : IDbConnectionFactory
{
    private string? _connectionString;

    public bool IsInitialized => !string.IsNullOrEmpty(_connectionString);
    public string ServerName { get; private set; } = "";
    public string DatabaseName { get; private set; } = "";

    public void Initialize(DbConnectionParamsDto parameters)
    {
        string serverAddress = string.IsNullOrWhiteSpace(parameters.Port)
            ? parameters.Server
            : $"{parameters.Server},{parameters.Port}";

        ServerName = serverAddress;
        DatabaseName = parameters.Database;

        _connectionString = new SqlConnectionStringBuilder
        {
            DataSource = serverAddress,
            InitialCatalog = parameters.Database,
            UserID = parameters.Username,
            Password = parameters.Password,
            TrustServerCertificate = true,
            Encrypt = false,
            MultiSubnetFailover = true,
            ConnectTimeout = 15
        }.ConnectionString;
    }

    public async Task<SqlConnection> BuildAndOpenConnectionAsync()
    {
        if (!IsInitialized)
            throw new InvalidOperationException("Підключення до БД не ініціалізовано. Спочатку виконайте вхід.");

        var connection = new SqlConnection(_connectionString);
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync();
        return connection;
    }
}
