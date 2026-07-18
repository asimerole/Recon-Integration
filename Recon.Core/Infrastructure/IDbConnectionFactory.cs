using Microsoft.Data.SqlClient;

namespace Recon.Core.Infrastructure;

public interface IDbConnectionFactory
{
    SqlConnection Create();
    void SetConnectionString(string connectionString);
    bool IsInitialized { get; }
}
