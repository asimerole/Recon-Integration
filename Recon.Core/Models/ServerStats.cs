namespace Recon.Core.Models;

public struct ServerStats
{
    public int DailyCollected { get; set; }
    public int DailyEmailed { get; set; }
    public int DailyIntegrated { get; set; }
    public int DailyUploaded { get; set; }
}