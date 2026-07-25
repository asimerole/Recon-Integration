using System.Text.Json.Serialization;

namespace Recon.Core.Options;

public class OneDriveConfig
{
    [JsonPropertyName("days")]
    public int Days { get; set; }

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;
}
