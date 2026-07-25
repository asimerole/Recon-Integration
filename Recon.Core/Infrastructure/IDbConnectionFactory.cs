using Microsoft.Data.SqlClient;
using Recon.Core.Dtos;

namespace Recon.Core.Infrastructure;

public interface IDbConnectionFactory
{
    Task<SqlConnection> BuildAndOpenConnectionAsync();
    void Initialize(DbConnectionParamsDto parameters);
    bool IsInitialized { get; }
    string ServerName { get; }
    string DatabaseName { get; }
}
