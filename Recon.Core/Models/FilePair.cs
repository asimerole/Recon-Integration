namespace Recon.Core.Models;

public class FilePair
{
    public DataFile? Data { get; set; }
    public ExpressFile? Express { get; set; }
    
    public ReconFile? Other { get; set; }
    
    public bool IsComplete => Data != null && Express != null;
    
    public int ReconNumber => Data?.ReconNumber ?? Express?.ReconNumber ?? 0;
    public DateTime Timestamp => Express?.Timestamp ?? Data?.Timestamp ?? DateTime.MinValue;
}