using System.Text.Json.Serialization;

namespace Recon.Core.Options;

public class OneDriveConfig
{
    [JsonPropertyName("months")]
    public int Months { get; set; }
    
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;
    
}