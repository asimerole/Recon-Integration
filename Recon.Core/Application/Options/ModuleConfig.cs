using System.Text.Json.Serialization;

namespace Recon.Core.Application.Options;

public class ModuleConfig
{
    // JsonPropertyName позволяет мапить "integrationIsActive" из JSON в C# свойство
    [JsonPropertyName("integrationIsActive")]
    public bool IsIntegrationActive { get; set; }

    [JsonPropertyName("ftpIsActive")]
    public bool IsFtpActive { get; set; }

    [JsonPropertyName("mailIsActive")]
    public bool IsMailActive { get; set; }

    [JsonPropertyName("oneDriveIsActive")]
    public bool IsOneDriveActive { get; set; }

    [JsonPropertyName("dbIsFull")]
    public bool DbIsFull { get; set; }
    
    [JsonPropertyName("fastDbBuild")]
    public bool FastDatabaseBuild { get; set; }

    // Set in Recon-Properties ("Пошта — обмежити контент по об'єктах"); read-only here,
    // SaveModuleConfigAsync never writes it.
    // Missing key = restricted: "everyone gets everything" must be an explicit choice
    [JsonPropertyName("mailRestrictionEnabled")]
    public bool MailRestrictionEnabled { get; set; } = true;
}