using System.Text.Json.Serialization;
using Recon.Core.Converters;

namespace Recon.Core.Options;

public class MailServerConfig
{
    [JsonPropertyName("SMTP server")]
    public string SmtpServer { get; set; }
    
    [JsonPropertyName("auth")]
    public bool UseAuth { get; set; } = false;
    
    [JsonPropertyName("email_sender")]
    public string EmailSender { get; set; } = string.Empty;
    
    [JsonPropertyName("name_sender")]
    public string NameSender { get; set; } = "Recon Service";
    
    [JsonPropertyName("ssl")]
    public bool UseSsl { get; set; } = false;

    [JsonPropertyName("port")] 
    [JsonConverter(typeof(JsonIntStringConverter))] 
    public int Port { get; set; } = 25;
    
    [JsonPropertyName("login")]
    public string AuthLogin { get; set; } = string.Empty;
    
    [JsonPropertyName("password")]
    public string AuthPassword { get; set; } = string.Empty;
    
}