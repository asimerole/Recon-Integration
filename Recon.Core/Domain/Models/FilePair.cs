namespace Recon.Core.Domain.Models;

public class FilePair
{
    public DataFile? Data { get; set; }
    public ExpressFile? Express { get; set; }

    public ReconFile? Other { get; set; }
    public bool IsComplete => Data != null && Express != null;
    public int ReconNumber => Data?.ReconNumber ?? Express?.ReconNumber  ?? Other?.ReconNumber ?? 0;

    public string Object => Data?.Object ?? Express?.Object  ?? Other?.Object ?? "";
    public string Substation => Data?.Substation ?? Express?.Substation  ?? Other?.Substation ?? "";

    // First known id: .meta of either file may carry it
    public int StructId => new[] { Data?.StructId, Express?.StructId, Other?.StructId }.FirstOrDefault(id => id > 0) ?? 0;

    /// <summary>
    /// Event time: from REXPR content if it was readable, otherwise RECON's mtime (the device's clock),
    /// and only then REXPR's mtime (for a generated REXPR that is the generation moment, i.e. wrong).
    /// </summary>
    public DateTime Timestamp =>
        Express is { HasParsedTimestamp: true } ? Express.Timestamp
        : Data != null ? Data.Timestamp
        : Express?.Timestamp ?? Other?.Timestamp ?? DateTime.MinValue;

    /// <summary>
    /// data.file_type. A pair and a lone RECON are 'RECON', a lone REXPR is 'REXPR' — Viewer opens records by these.
    /// Others keep their 5-char prefix ('DAILY', 'RNET_', 'RPUSK', 'DIAGN').
    /// </summary>
    public string FileType =>
        Data != null ? "RECON"
        : Express != null ? "REXPR"
        : string.IsNullOrEmpty(Other?.FilePrefix) ? "UNKNOWN" : Other.FilePrefix;
}
