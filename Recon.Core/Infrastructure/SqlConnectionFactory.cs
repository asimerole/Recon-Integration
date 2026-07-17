using Microsoft.Data.SqlClient;
using Recon.Core.Dtos;
using System.Data;

namespace Recon.Core.Infrastructure;

public class SqlConnectionFactory : IDbConnectionFactory
{
    private string? _connectionString;

    public bool IsInitialized => !string.IsNullOrEmpty(_connectionString);

    public void Initialize(DbConnectionParamsDto parameters)
    {
        string serverAddress = string.IsNullOrWhiteSpace(parameters.Port)
        ? parameters.Server
        : $"{parameters.Server},{parameters.Port}";

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = serverAddress,
            InitialCatalog = parameters.Database,
            UserID = parameters.Username,
            Password = parameters.Password,
            TrustServerCertificate = true,
            Encrypt = false,
            MultiSubnetFailover = true,
            ConnectTimeout = 15
        };

        _connectionString = builder.ConnectionString;
    }

    public async Task<SqlConnection> BuildAndOpenConnectionAsync()
    {
        if (!IsInitialized)
        {
            throw new InvalidOperationException("DatabaseConnectionProvider is not initialized. Call Initialize() first.");
        }

        var connection = new SqlConnection(_connectionString);

        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        return connection;
    }
}
