using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using FluentFTP;
using Recon.Core.Application.Interfaces;
using Recon.Core.Application.Interfaces.Repositories;
using Recon.Core.Domain.Constants;
using Recon.Core.Domain.Enums;
using Recon.Core.Domain.Models;

namespace Recon.Core.Application.Services;

/// <summary>
/// Collects files from registrators' FTP servers into root_directory\Cache (integration takes them from there).
/// Per file: wait until stable → download to .tmp → check size → write .meta → move into Cache → delete on device.
/// The device copy is deleted only after the local one is safe, so a failed step means "download again", never loss.
/// </summary>
public class FtpService : IFtpService
{
    private static readonly string[] ValidPrefixes = ["REXPR", "RECON", "RNET", "RPUSK", "DAILY", "DIAGN"];
    // Different IPs are polled in parallel; servers sharing one IP (several objects) stay sequential —
    // registrators accept only a couple of FTP sessions
    private const int MaxParallelServers = 8;
    private static readonly TimeSpan MinCycleDelay = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions MetaJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly IConfigRepository _configRepository;
    private readonly IServerRepository _serverRepository;
    private readonly IAppLogRepository _appLog;
    private readonly IStatisticsService _statsService;
    private readonly IMailService _mailService;

    internal FtpFileGate Gate { get; } = new();
    private readonly ServerFailureTracker _failures = new();

    private readonly object _stateLock = new();
    private CancellationTokenSource? _cts;

    public FtpService(IConfigRepository configRepository, IServerRepository serverRepository,
        IAppLogRepository appLog, IStatisticsService statsService, IMailService mailService)
    {
        _configRepository = configRepository;
        _serverRepository = serverRepository;
        _appLog = appLog;
        _statsService = statsService;
        _mailService = mailService;
    }

    private bool IsRunning => _cts is { IsCancellationRequested: false };

    public void StartFTP()
    {
        lock (_stateLock)
        {
            if (IsRunning) return;
            _cts = new CancellationTokenSource();
        }
        var token = _cts.Token;
        Task.Run(() => WorkerLoopAsync(token));
        _ = _appLog.LogAsync(LogServiceId.Ftp, "ftp_start");
    }

    public void StopFTP()
    {
        lock (_stateLock)
        {
            if (!IsRunning) return;
            _cts!.Cancel();
            _cts = null;
        }
    }

    // ───────────── cycle ─────────────

    private async Task WorkerLoopAsync(CancellationToken token)
    {
        bool rootMissingLogged = false;
        while (!token.IsCancellationRequested)
        {
            try
            {
                var config = await _configRepository.GetModuleConfigAsync();
                if (!config.IsFtpActive)
                {
                    await _appLog.LogAsync(LogServiceId.Ftp, "ftp_stop", "Модуль вимкнено в налаштуваннях");
                    StopFTP();
                    return;
                }

                string rootFolder = await _configRepository.GetRootFolderAsync();
                if (string.IsNullOrWhiteSpace(rootFolder))
                {
                    // Path.Combine("", "Cache") would put files into the process directory
                    if (!rootMissingLogged)
                        await _appLog.LogAsync(LogServiceId.Ftp, "loop_error", "Не задано root_directory, збір зупинено до налаштування");
                    rootMissingLogged = true;
                    await Task.Delay(TimeSpan.FromMinutes(1), token);
                    continue;
                }
                rootMissingLogged = false;

                string cacheDir = Path.Combine(rootFolder, "Cache");
                Directory.CreateDirectory(cacheDir);

                var servers = await _serverRepository.GetAllServersAsync();
                Gate.BeginCycle();

                await Parallel.ForEachAsync(
                    servers.GroupBy(s => s.IpAddress),
                    new ParallelOptions { MaxDegreeOfParallelism = MaxParallelServers, CancellationToken = token },
                    async (sameIp, ct) =>
                    {
                        foreach (var server in sameIp)
                        {
                            ct.ThrowIfCancellationRequested();
                            EnsureLocalFolder(server, rootFolder);
                            await ProcessServerAsync(server, cacheDir, ct);
                        }
                    });

                Gate.EndCycle();

                int feedingTime = await _configRepository.GetFeedingTimeAsync();
                var delay = TimeSpan.FromSeconds(feedingTime);
                await Task.Delay(delay < MinCycleDelay ? MinCycleDelay : delay, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Used to be outside the loop: one DB hiccup stopped collection until the app was restarted
                await _appLog.LogAsync(LogServiceId.Ftp, "loop_error", ex.Message);
                try { await Task.Delay(TimeSpan.FromSeconds(30), token); } catch (OperationCanceledException) { break; }
            }
        }
        await _appLog.LogAsync(LogServiceId.Ftp, "ftp_stop");
    }

    // Integration moves files to struct.files_path (server.LocalFolderPath); make sure it exists
    private void EnsureLocalFolder(ServerInfo server, string rootFolder)
    {
        try
        {
            string path = !string.IsNullOrWhiteSpace(server.LocalFolderPath)
                ? server.LocalFolderPath
                : Path.Combine(rootFolder, DirTreeOf(server));
            Directory.CreateDirectory(path);
        }
        catch (Exception ex)
        {
            _ = _appLog.LogAsync(LogServiceId.Ftp, "file_error", $"Не вдалося створити папку {server.Unit}/{server.Object}: {ex.Message}");
        }
    }

    // Unit "ОСР - ДТЕК" → ОСР\ДТЕК (the same " - " integration joins with); a hyphen inside a name stays
    internal static string DirTreeOf(ServerInfo server)
    {
        var parts = server.Unit.Split(" - ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Append(server.Substation)
            .Append(server.Object)
            .Select(Sanitize);
        return Path.Combine(parts.ToArray());
    }

    private static string Sanitize(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Unknown";
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim();
    }

    // ───────────── one server ─────────────

    private async Task ProcessServerAsync(ServerInfo server, string cacheDir, CancellationToken token)
    {
        string serverKey = $"{server.IpAddress} ({server.Substation}/{server.Object})";
        try
        {
            using var client = new AsyncFtpClient(server.IpAddress, server.Login, server.Password);
            ConfigureClient(client);
            await client.Connect(token);

            await _serverRepository.UpdateServerStatusAsync(server.StructId, lastPing: DateTime.Now);
            if (_failures.OnSuccess(serverKey, DateTime.Now) is { } downtime)
                await _appLog.LogAsync(LogServiceId.Ftp, "server_restored",
                    $"{serverKey}: зв'язок відновлено (не було {FormatDuration(downtime)})");

            var items = await client.GetListing(server.RemoteFolderPath, token);
            foreach (var item in items)
            {
                token.ThrowIfCancellationRequested();
                if (item.Type != FtpObjectType.File || !HasValidPrefix(item.Name)) continue;

                string gateKey = $"{server.IpAddress}|{item.FullName}";
                if (!Gate.IsStable(gateKey, item.Size, item.Modified)) continue;   // next cycle

                if (await DownloadFileAsync(client, item, server, cacheDir, token))
                    Gate.Forget(gateKey);
            }

            await client.Disconnect(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            switch (_failures.OnFailure(serverKey, DateTime.Now))
            {
                case ServerFailureTracker.Decision.FirstFailure:
                    await _appLog.LogAsync(LogServiceId.Ftp, "server_error", $"{serverKey}: {ex.Message}");
                    break;
                case ServerFailureTracker.Decision.Reminder:
                    var since = _failures.DownSince(serverKey) ?? DateTime.Now;
                    await _appLog.LogAsync(LogServiceId.Ftp, "server_error",
                        $"{serverKey}: досі недоступний ({FormatDuration(DateTime.Now - since)}): {ex.Message}");
                    break;
            }
        }
    }

    private static bool HasValidPrefix(string fileName) =>
        ValidPrefixes.Any(p => fileName.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    private static void ConfigureClient(AsyncFtpClient client)
    {
        client.Config.ConnectTimeout = 10000;
        client.Config.ReadTimeout = 30000;
        client.Config.DataConnectionConnectTimeout = 10000;
        client.Config.DataConnectionReadTimeout = 30000;
        client.Config.RetryAttempts = 3;
        client.Encoding = Encoding.UTF8;
        client.Config.ValidateAnyCertificate = true;
        client.Config.DataConnectionType = FtpDataConnectionType.AutoPassive;
        client.Config.TimeConversion = FtpDate.LocalTime;
    }

    // ───────────── one file ─────────────

    /// <returns>true when the file is in Cache (and removed from the device or will be re-downloaded)</returns>
    private async Task<bool> DownloadFileAsync(AsyncFtpClient client, FtpListItem item, ServerInfo server,
        string cacheDir, CancellationToken token)
    {
        string finalPath = Path.Combine(cacheDir, item.Name);
        string tempPath = finalPath + ".tmp";

        // The previous file with this name hasn't been taken by integration yet (integration off, or two
        // objects with the same recon number). Overwriting it would lose that event — take this one later.
        if (File.Exists(finalPath)) return false;

        try
        {
            if (!client.IsConnected) await client.Connect(token);

            var status = await client.DownloadFile(tempPath, item.FullName, FtpLocalExists.Overwrite, token: token);
            if (status != FtpStatus.Success)
            {
                await _appLog.LogAsync(LogServiceId.Ftp, "file_error", $"{item.Name}: завантаження не вдалося ({status})");
                TryDelete(tempPath);
                return false;
            }

            long localSize = new FileInfo(tempPath).Length;
            if (item.Size >= 0 && localSize != item.Size)
            {
                await _appLog.LogAsync(LogServiceId.Ftp, "file_error",
                    $"{item.Name}: розмір {localSize} ≠ {item.Size} на сервері, файл залишено на реєстраторі");
                TryDelete(tempPath);
                return false;
            }

            if (item.Modified > DateTime.MinValue)
                File.SetLastWriteTime(tempPath, item.Modified);   // event time; integration sorts by it

            // .meta first: integration must never see the file without its target folder
            await File.WriteAllTextAsync(finalPath + ".meta",
                JsonSerializer.Serialize(new { targetPath = server.LocalFolderPath, structId = server.StructId }, MetaJson), token);
            File.Move(tempPath, finalPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _appLog.LogAsync(LogServiceId.Ftp, "file_error", $"{item.Name}: {ex.Message}");
            TryDelete(tempPath);
            return false;
        }

        // Only now the file is safe locally
        try
        {
            await client.DeleteFile(item.FullName, token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Not fatal: the next cycle downloads it again, integration and the mail outbox drop the duplicate
            await _appLog.LogAsync(LogServiceId.Ftp, "file_warn",
                $"{item.Name}: не вдалося видалити на реєстраторі, буде завантажено повторно: {ex.Message}");
        }

        await RegisterCollectedAsync(item, server, finalPath);
        return true;
    }

    private async Task RegisterCollectedAsync(FtpListItem item, ServerInfo server, string finalPath)
    {
        bool isEvent = item.Name.StartsWith("RECON", StringComparison.OrdinalIgnoreCase)
                    || item.Name.StartsWith("REXPR", StringComparison.OrdinalIgnoreCase);
        bool isDaily = item.Name.StartsWith("DAILY", StringComparison.OrdinalIgnoreCase);

        await _serverRepository.UpdateDailyStatAsync(server.StructId, "collected");
        await _serverRepository.UpdateServerStatusAsync(
            server.StructId,
            lastPing: DateTime.Now,
            lastRecon: isEvent ? DateTime.Now : null,
            lastDaily: isDaily ? (item.Modified > DateTime.MinValue ? item.Modified : DateTime.Now) : null);
        _statsService.RegisterAction(ServiceType.Ftp);

        if (isDaily) await SendDailyFileAsync(server, finalPath);
    }

    private async Task SendDailyFileAsync(ServerInfo server, string filePath)
    {
        // Mail module is off: don't mark the daily file as sent, it was never sent
        if (!_mailService.IsRunning) return;

        try
        {
            bool shouldSend = await _serverRepository.TryMarkDailySentAsync(server.StructId);
            if (!shouldSend) return;

            // Not awaited: the file is snapshotted synchronously inside, sending runs in background
            _ = _mailService.SendDailyFileAsync(server, filePath);
        }
        catch (Exception ex)
        {
            await _appLog.LogAsync(LogServiceId.Ftp, "file_error", $"Відправка суточного файлу {server.Object}: {ex.Message}");
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static string FormatDuration(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays} дн. {t.Hours} год."
        : t.TotalHours >= 1 ? $"{(int)t.TotalHours} год. {t.Minutes} хв."
        : $"{Math.Max(1, (int)t.TotalMinutes)} хв.";
}
