using System.Text.Json;
using Recon.Core.Application.Options;
using Recon.Core.Infrastructure.Repositories;

namespace Recon.Core.Tests;

// Rows of access_settings as Recon-Properties (WPF) and the old C++ Properties write them
public class SettingsDeserializationTests
{
    private static T Parse<T>(string json) => JsonSerializer.Deserialize<T>(json, ConfigRepository.JsonOptions)!;

    [Fact]
    public void OneDrive_new_properties_format()
    {
        var c = Parse<OneDriveConfig>("""{"local_path":"C:\\Users\\x\\OneDrive","AdminEmail":"a","TenantId":"t","ClientId":"i","ClientSecret":"s","days":30}""");
        Assert.Equal(@"C:\Users\x\OneDrive", c.Path);
        Assert.Equal(30, c.Days);
    }

    [Fact]
    public void OneDrive_old_cpp_format_months_become_days()
    {
        var c = Parse<OneDriveConfig>("""{"path":"D:\\Old","months":2}""");
        Assert.Equal(@"D:\Old", c.Path);
        Assert.Equal(60, c.Days);
    }

    [Fact]
    public void Mail_template_and_string_port()
    {
        var c = Parse<MailServerConfig>("""{"SMTP server":"smtp.x","port":"25","template":"Hello"}""");
        Assert.Equal("smtp.x", c.SmtpServer);
        Assert.Equal(25, c.Port);
        Assert.Equal("Hello", c.MsgTemplate);
    }

    [Fact]
    public void Mail_old_msg_template_key()
    {
        Assert.Equal("Old", Parse<MailServerConfig>("""{"msg_template":"Old"}""").MsgTemplate);
    }

    [Fact]
    public void Missing_mail_restriction_key_means_restricted()
    {
        var c = Parse<ModuleConfig>("""{"integrationIsActive":true,"mailIsActive":true}""");
        Assert.True(c.MailRestrictionEnabled);
    }

    [Fact]
    public void Explicit_mail_restriction_off_is_respected()
    {
        Assert.False(Parse<ModuleConfig>("""{"mailRestrictionEnabled":false}""").MailRestrictionEnabled);
    }
}
