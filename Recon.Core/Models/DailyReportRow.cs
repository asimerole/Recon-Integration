namespace Recon.Core.Models;

public class DailyReportRow
{
    public string Unit { get; set; } = "";
    public string Substation { get; set; } = "";
    public string Object { get; set; } = "";
    public DateTime? LastPing { get; set; }
    public DateTime? LastRecon { get; set; }
    public DateTime? LastDaily { get; set; }
    public int Collected { get; set; }
    public int Integrated { get; set; }
    public bool HadActivityYesterday { get; set; }

    public bool HasPingToday => LastPing.HasValue && LastPing.Value.Date == DateTime.Today;
    public bool IsNewlyDead => !HasPingToday && HadActivityYesterday;
    public bool IsRestored => HasPingToday && !HadActivityYesterday;
}
