using System.Text.Json.Serialization;

namespace Recon.Core.Application.Options;

public class OneDriveConfig
{
    // Recon-Properties (WPF) writes "local_path" and "days"
    [JsonPropertyName("local_path")]
    public string LocalPath { get; set; } = string.Empty;

    [JsonPropertyName("days")]
    public int? DaysValue { get; set; }

    // Old C++ Properties wrote "path" and "months"
    [JsonPropertyName("path")]
    public string LegacyPath { get; set; } = string.Empty;

    [JsonPropertyName("months")]
    public int? LegacyMonths { get; set; }

    [JsonIgnore]
    public string Path => !string.IsNullOrWhiteSpace(LocalPath) ? LocalPath : LegacyPath;

    [JsonIgnore]
    public int Days => DaysValue ?? LegacyMonths * 30 ?? 0;
}
