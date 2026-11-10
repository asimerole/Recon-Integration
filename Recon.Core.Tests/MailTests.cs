using Recon.Core.Application.Options;
using Recon.Core.Application.Services;
using Recon.Core.Domain.Models;

namespace Recon.Core.Tests;

public class MailComposerTests
{
    private static MailOutboxItem Item(int minute, params string[] files) => new()
    {
        Id = minute, ReconId = 353, Object = "Вузлова", Substation = "ДТЕК",
        EventTime = new DateTime(2026, 11, 7, 10, minute, 0),
        FilePaths = string.Join(MailOutboxItem.PathSeparator, files),
    };

    [Fact]
    public void Recipients_without_domain_are_invalid()
    {
        var (valid, invalid) = MailComposer.ParseRecipients(["a@ua.energy", "admin", " ", "A@ua.energy", "b@ua.energy"]);

        Assert.Equal(["a@ua.energy", "b@ua.energy"], valid.Select(v => v.Address));   // duplicate (case) removed
        Assert.Equal(["admin"], invalid);
    }

    [Fact]
    public void Burst_of_one_object_goes_into_one_mail()
    {
        var items = Enumerable.Range(0, 5).Select(i => Item(i, $"RECON353.{i}", $"REXPR353.{i}")).ToList();

        var batches = MailComposer.BuildBatches(items, _ => 1000);

        Assert.Single(batches);
        Assert.Equal(5, batches[0].Count);
    }

    [Fact]
    public void Batches_respect_file_count_and_never_split_a_pair()
    {
        var items = Enumerable.Range(0, 15).Select(i => Item(i, $"RECON353.{i}", $"REXPR353.{i}")).ToList(); // 30 files

        var batches = MailComposer.BuildBatches(items, _ => 1000, maxFiles: 20);

        Assert.Equal([10, 5], batches.Select(b => b.Count));
        Assert.All(batches, b => Assert.True(b.Sum(i => i.Files.Length) <= 20));
    }

    [Fact]
    public void Batches_respect_size_limit_but_a_single_big_event_still_goes()
    {
        var items = new[] { Item(1, "big"), Item(2, "small1"), Item(3, "small2") };

        var batches = MailComposer.BuildBatches(items, f => f == "big" ? 20_000_000 : 1_000, maxBytes: 15_000_000);

        Assert.Equal([1, 2], batches.Select(b => b.Count));
    }

    [Fact]
    public void Subject_shows_latest_event_and_count()
    {
        var subject = MailComposer.EventSubject([Item(5, "a"), Item(9, "b")]);
        Assert.Equal("Аварійні файли: ДТЕК - Вузлова | 07.11.2026 10:09 (2 подій)", subject);
    }

    [Fact]
    public void Body_lists_every_event_and_missing_files()
    {
        var body = MailComposer.EventBody([Item(1, @"C:\d\RECON353.1", @"C:\d\REXPR353.1")], "Шаблон", [@"C:\d\REXPR353.1"]);

        Assert.StartsWith("Шаблон", body);
        Assert.Contains("07.11.2026 10:01:00 — RECON353.1, REXPR353.1", body);
        Assert.Contains("Не вдалося прикріпити (файл не знайдено): REXPR353.1", body);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(3, 8)]
    [InlineData(6, 60)]
    [InlineData(20, 60)]
    public void Retry_delay_grows_to_an_hour(int attempts, int minutes)
    {
        Assert.Equal(TimeSpan.FromMinutes(minutes), MailComposer.RetryDelay(attempts));
    }
}

public sealed class MailSendingTests : IDisposable
{
    private readonly FakeSmtpServer _smtp = new();

    public void Dispose() => _smtp.Dispose();

    private MailService Create()
    {
        var mail = new MailServerConfig { SmtpServer = "127.0.0.1", Port = _smtp.Port, EmailSender = "recon@ua.energy", NameSender = "Recon" };
        return new MailService(new FakeAppLog(), new FakeFileData([]),
            new FakeConfig("", new OneDriveConfig(), mail), new FakeServers(), new FakeUsers(), new FakeOutbox());
    }

    [Fact]
    public async Task One_refused_address_does_not_stop_the_others()
    {
        var result = await Create().SendToAllAsync(["a@ua.energy", "bad@ua.energy", "admin", "c@ua.energy"], "Тест", "Текст");

        Assert.True(result.Success);
        Assert.Equal(2, result.Sent);
        Assert.Single(result.Rejected);
        Assert.StartsWith("bad@ua.energy", result.Rejected[0]);
        Assert.Equal(["admin"], result.Invalid);

        var message = Assert.Single(_smtp.Messages);
        Assert.Equal(["a@ua.energy", "c@ua.energy"], message.Rcpt);
    }

    [Fact]
    public async Task Sender_is_not_a_recipient_and_recipients_are_hidden()
    {
        await Create().SendToAllAsync(["a@ua.energy"], "Тест", "Текст");

        var message = Assert.Single(_smtp.Messages);
        Assert.DoesNotContain("recon@ua.energy", message.Rcpt);
        Assert.Matches(@"To: Undisclosed recipients:\s*;", message.Data);
        Assert.DoesNotContain("a@ua.energy", message.Data);   // Bcc never reaches the headers
    }

    [Fact]
    public async Task Large_recipient_list_is_split_into_messages_of_50()
    {
        var recipients = Enumerable.Range(1, 120).Select(i => $"user{i}@ua.energy");

        var result = await Create().SendToAllAsync(recipients, "Тест", "Текст");

        Assert.Equal(120, result.Sent);
        Assert.Equal([50, 50, 20], _smtp.Messages.Select(m => m.Rcpt.Count));
    }

    [Fact]
    public async Task Server_down_is_reported_not_success()
    {
        _smtp.Dispose();

        var result = await Create().SendToAllAsync(["a@ua.energy"], "Тест", "Текст");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Attachment_is_sent()
    {
        string file = Path.Combine(Path.GetTempPath(), $"RECON353.{Guid.NewGuid():N}"[..20]);
        File.WriteAllText(file, "payload");
        try
        {
            await Create().SendToAllAsync(["a@ua.energy"], "Тест", "Текст", [file]);
            Assert.Contains(Path.GetFileName(file), Assert.Single(_smtp.Messages).Data);
        }
        finally { File.Delete(file); }
    }
}
