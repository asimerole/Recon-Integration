using System.Text.Json.Serialization;
using Recon.Core.Application.Converters;

namespace Recon.Core.Application.Options;

public class MailServerConfig
{
    [JsonPropertyName("SMTP server")]
    public string SmtpServer { get; set; } = string.Empty;
    
    [JsonPropertyName("auth")]
    public bool UseAuth { get; set; } = false;
    
    [JsonPropertyName("email_sender")]
    public string EmailSender { get; set; } = string.Empty;
    
    // Recon-Properties (WPF) writes "template", old C++ Properties wrote "msg_template"
    [JsonPropertyName("template")]
    public string Template { get; set; } = string.Empty;

    [JsonPropertyName("msg_template")]
    public string LegacyMsgTemplate { get; set; } = string.Empty;

    [JsonIgnore]
    public string MsgTemplate => !string.IsNullOrEmpty(Template) ? Template : LegacyMsgTemplate;
    
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