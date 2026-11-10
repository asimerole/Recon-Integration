using System.IO;
using System.Text;
using MimeKit;
using Recon.Core.Domain.Models;

namespace Recon.Core.Application.Services;

/// <summary>Pure mail logic (no SMTP, no DB): recipients, splitting into e-mails, texts, retry timing.</summary>
internal static class MailComposer
{
    public const int MaxFilesPerMail = 20;
    public const long MaxBytesPerMail = 15L * 1024 * 1024;   // below common 20-25 MB server limits after base64
    public const int RecipientsPerMessage = 50;

    // MimeKit accepts "admin" (no domain) by default; the SMTP server would reject it at RCPT time
    private static readonly ParserOptions StrictAddress = new() { AllowAddressesWithoutDomain = false };

    public static (List<MailboxAddress> Valid, List<string> Invalid) ParseRecipients(IEnumerable<string> logins)
    {
        var valid = new List<MailboxAddress>();
        var invalid = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in logins)
        {
            string login = raw?.Trim() ?? string.Empty;
            if (login.Length == 0 || !seen.Add(login)) continue;

            if (login.Contains('@') && MailboxAddress.TryParse(StrictAddress, login, out var address))
                valid.Add(address);
            else
                invalid.Add(login);
        }
        return (valid, invalid);
    }

    /// <summary>
    /// Splits one object's events into e-mails: in event order, at most <see cref="MaxFilesPerMail"/> files and
    /// <see cref="MaxBytesPerMail"/> bytes each. An event is never split between two e-mails.
    /// </summary>
    public static List<List<MailOutboxItem>> BuildBatches(IEnumerable<MailOutboxItem> items, Func<string, long> fileSize,
        int maxFiles = MaxFilesPerMail, long maxBytes = MaxBytesPerMail)
    {
        var batches = new List<List<MailOutboxItem>>();
        var current = new List<MailOutboxItem>();
        int files = 0;
        long bytes = 0;

        foreach (var item in items.OrderBy(i => i.EventTime))
        {
            int itemFiles = item.Files.Length;
            long itemBytes = item.Files.Sum(fileSize);

            if (current.Count > 0 && (files + itemFiles > maxFiles || bytes + itemBytes > maxBytes))
            {
                batches.Add(current);
                current = [];
                files = 0;
                bytes = 0;
            }

            current.Add(item);
            files += itemFiles;
            bytes += itemBytes;
        }

        if (current.Count > 0) batches.Add(current);
        return batches;
    }

    public static string EventSubject(IReadOnlyList<MailOutboxItem> batch)
    {
        var first = batch[0];
        // Device clocks drift: never show a time in the future
        DateTime latest = Min(batch.Max(i => i.EventTime), DateTime.Now);
        string where = string.IsNullOrEmpty(first.Substation) ? first.Object : $"{first.Substation} - {first.Object}";
        string count = batch.Count > 1 ? $" ({batch.Count} подій)" : "";
        return $"Аварійні файли: {where} | {latest:dd.MM.yyyy HH:mm}{count}";
    }

    public static string EventBody(IReadOnlyList<MailOutboxItem> batch, string template, IReadOnlyCollection<string> missingFiles)
    {
        var first = batch[0];
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(template)) sb.AppendLine(template.TrimEnd()).AppendLine();

        sb.AppendLine($"Об'єкт: {first.Substation} / {first.Object}");
        sb.AppendLine($"Подій: {batch.Count}");
        sb.AppendLine();
        foreach (var item in batch.OrderBy(i => i.EventTime))
            sb.AppendLine($"  • {item.EventTime:dd.MM.yyyy HH:mm:ss} — {string.Join(", ", item.Files.Select(Path.GetFileName))}");

        if (missingFiles.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"⚠ Не вдалося прикріпити (файл не знайдено): {string.Join(", ", missingFiles.Select(Path.GetFileName))}");
        }

        sb.AppendLine();
        sb.AppendLine("Це повідомлення сформовано автоматично.");
        return sb.ToString();
    }

    /// <summary>1, 2, 4 … 60 minutes between attempts.</summary>
    public static TimeSpan RetryDelay(int attemptsSoFar) =>
        TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, Math.Min(attemptsSoFar, 6))));

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
}
