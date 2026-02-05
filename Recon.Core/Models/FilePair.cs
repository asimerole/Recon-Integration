namespace Recon.Core.Models;

public class FilePair
{
    public DataFile? Data { get; set; }
    public ExpressFile? Express { get; set; }
    
    public ReconFile? Other { get; set; }
    public bool IsComplete => Data != null && Express != null;
    public int ReconNumber => Data?.ReconNumber ?? Express?.ReconNumber  ?? Other?.ReconNumber ?? 0;
    
    public string Object => Data?.Object ?? Express?.Object  ?? Other?.Object ?? "";
    public string Substation => Data?.Substation ?? Express?.Substation  ?? Other?.Substation ?? "";
    public int ServerId => Data?.ServerId ?? Express?.ServerId ?? Other?.ServerId ?? 0;
    public DateTime Timestamp => Express?.Timestamp ?? Data?.Timestamp ?? Other?.Timestamp ?? DateTime.MinValue;
}