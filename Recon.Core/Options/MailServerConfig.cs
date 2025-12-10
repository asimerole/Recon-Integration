using System.Text.Json.Serialization;

namespace Recon.Core.Options;

public class MailServerConfig
{
    [JsonPropertyName("SMTP server")]
    public int SmtpServer { get; set; }
    
    [JsonPropertyName("auth")]
    public bool Auth { get; set; } = false;
    
    [JsonPropertyName("email_sender")]
    public string EmailSender { get; set; } = string.Empty;
    
    [JsonPropertyName("name_sender")]
    public string NameSender { get; set; } = string.Empty;
    
    [JsonPropertyName("ssl")]
    public bool Ssl { get; set; } = false;
    
    [JsonPropertyName("port")]
    public int Port { get; set; } = 0;
    
    [JsonPropertyName("login")]
    public string AuthLogin { get; set; } = string.Empty;
    
    [JsonPropertyName("password")]
    public string AuthPassword { get; set; } = string.Empty;
    
}