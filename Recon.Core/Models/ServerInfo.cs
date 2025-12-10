using Recon.Core.Enums;

namespace Recon.Core.Models;

public class ServerInfo
{
    public int Id { get; set; }
    public string Unit { get; set; } = string.Empty;
    public string Substation { get; set; } = string.Empty;
    public string Object { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public string Login { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string RemoteFolderPath { get; set; } = string.Empty;
    public string LocalFolderPath { get; set; } = string.Empty;
    public bool IsFourDigits { get; set; }
    
    
    public ServerStatus Status { get; set; }
    public int ReconId { get; set; } = 0;
    public int StructId { get; set; } = 0;
    
    public DateTime LastPingTime { get; set; }
    public DateTime LastDailyFileDate { get; set; }
    
    public ServerStats ServerStats { get; set; } = new ServerStats();
    public List<DateTime> CollectedTimestamps { get; set; } = new List<DateTime>();
    
}