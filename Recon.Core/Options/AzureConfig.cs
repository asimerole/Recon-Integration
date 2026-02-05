using System.Text.Json.Serialization;

namespace Recon.Core.Options;

public class AzureConfig
{
    [JsonPropertyName("TenantId")]

    public string TenantId { get; set; }
    [JsonPropertyName("ClientId")]

    public string ClientId { get; set; }
    [JsonPropertyName("ClientSecret")]

    public string ClientSecret { get; set; }
    [JsonPropertyName("AdminEmail")]

    public string AdminEmail { get; set; }
    [JsonPropertyName("path")]

    public string OneDrivePath { get; set; }
}