namespace Recon.Core.Domain.Models;

/// <summary>One event (RECON/REXPR pair) waiting in mail_outbox.</summary>
public class MailOutboxItem
{
    public long Id { get; set; }
    public int ReconId { get; set; }
    public string Object { get; set; } = string.Empty;
    public string? Substation { get; set; }
    public DateTime EventTime { get; set; }
    public string FilePaths { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public int Attempts { get; set; }

    public const char PathSeparator = '|';

    public string[] Files => FilePaths.Split(PathSeparator, StringSplitOptions.RemoveEmptyEntries);
}

public enum MailOutboxStatus : byte
{
    Pending = 0,
    Sent = 1,
    Failed = 2,
    Skipped = 3,
}
