namespace Recon.Core.Options;

public class DatabaseOptions
{
    public const string SectionName = "database";
    
    public string ConnectionString { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Server { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public int Port { get; set; } = 25;
    
    public int CommandTimeout { get; set; } = 30;
    public string ProviderName { get; set; } = "System.Data.SqlClient";
}