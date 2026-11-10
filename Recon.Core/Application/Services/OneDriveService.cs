using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Recon.Core.Application.Interfaces;
using Recon.Core.Application.Interfaces.Repositories;
using Recon.Core.Application.Options;
using Recon.Core.Domain.Constants;
using Recon.Core.Domain.Enums;

namespace Recon.Core.Application.Services;

/// <summary>
/// Mirrors files that integration placed into the storage (root_directory) into the local OneDrive folder,
/// keeping the same relative path, so Recon-Properties can share folders by that path.
///   - copying runs in a background queue, integration never waits for the OneDrive disk;
///   - a file locked by the OneDrive client is retried with back-off;
///   - catch-up copies what was missed (module off, errors) — only month folders inside the retention window;
///   - nightly cleanup deletes old files but never folders: sharing permissions are bound to folder IDs.
/// </summary>
public class OneDriveService : IOneDriveService
{
    private static readonly TimeSpan ConfigTtl = TimeSpan.FromMinutes(1);
    private static readonly Regex MonthFolder = new(@"^(\d{4})_(\d{2})$", RegexOptions.Compiled);

    // internal: tests shorten them
    internal TimeSpan[] RetryDelays { get; set; } =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2)];
    internal TimeSpan CatchUpInterval { get; set; } = TimeSpan.FromHours(6);

    private readonly IAppLogRepository _appLog;
    private readonly IConfigRepository _configRepository;
    private readonly IFileDataRepository _fileDataRepository;
    private readonly IServerRepository _serverRepository;
    private readonly IStatisticsService _statsService;

    private OneDriveConfig? _config;
    private DateTime _configLoadedAt;
    private bool _pathMissingLogged;

    private readonly object _stateLock = new();
    private CancellationTokenSource? _cts;
    private Channel<CopyJob>? _queue;

    internal sealed record CopyJob(string Source, string StorageRoot, int StructId, int Attempt = 0);
    internal enum CopyResult { Copied, UpToDate, Busy, Failed, NotConfigured }

    public OneDriveService(IAppLogRepository appLog, IConfigRepository configRepository,
        IFileDataRepository fileDataRepository, IServerRepository serverRepository, IStatisticsService statsService)
    {
        _appLog = appLog;
        _configRepository = configRepository;
        _fileDataRepository = fileDataRepository;
        _serverRepository = serverRepository;
        _statsService = statsService;
    }

    public bool IsRunning => _cts is { IsCancellationRequested: false };

    public void Start()
    {
        lock (_stateLock)
        {
            if (IsRunning) return;
            _cts = new CancellationTokenSource();
            _queue = Channel.CreateUnbounded<CopyJob>(new UnboundedChannelOptions { SingleReader = true });
        }

        var token = _cts.Token;
        var queue = _queue;
        Task.Run(() => CopyWorkerAsync(queue, token));
        Task.Run(() => CatchUpLoopAsync(token));
        Task.Run(() => CleanupLoopAsync(token));
        _ = _appLog.LogAsync(LogServiceId.OneDrive, "onedrive_start");
    }

    public void Stop()
    {
        lock (_stateLock)
        {
            if (!IsRunning) return;
            _cts!.Cancel();
            _queue!.Writer.TryComplete();
            _cts = null;
            _queue = null;
        }
        // Jobs still queued are dropped on purpose: the next catch-up finds them missing
        _ = _appLog.LogAsync(LogServiceId.OneDrive, "onedrive_stop");
    }

    public void Enqueue(string sourceFullPath, string storageRoot, int structId = 0)
    {
        // Module off: catch-up picks the file up after it is switched on
        _queue?.Writer.TryWrite(new CopyJob(sourceFullPath, storageRoot, structId));
    }

    // ───────────── copy queue ─────────────

    private async Task CopyWorkerAsync(Channel<CopyJob> queue, CancellationToken token)
    {
        try
        {
            await foreach (var job in queue.Reader.ReadAllAsync(token))
            {
                try
                {
                    await ProcessJobAsync(job, queue, token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await _appLog.LogAsync(LogServiceId.OneDrive, "onedrive_error", $"{Path.GetFileName(job.Source)}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ProcessJobAsync(CopyJob job, Channel<CopyJob> queue, CancellationToken token)
    {
        var config = await GetConfigAsync();
        var (result, relativePath, error) = CopyOnce(job, config.Path);

        switch (result)
        {
            case CopyResult.Copied:
                await _appLog.LogAsync(LogServiceId.OneDrive, "onedrive_copied", relativePath);
                await _serverRepository.UpdateDailyStatAsync(job.StructId, "uploaded");
                _statsService.RegisterAction(ServiceType.OneDrive);
                break;

            case CopyResult.Busy when job.Attempt < RetryDelays.Length:
                // Don't block the queue while waiting: re-add the job after the delay
                _ = RequeueLaterAsync(queue, job with { Attempt = job.Attempt + 1 }, RetryDelays[job.Attempt], token);
                break;

            case CopyResult.Busy:
            case CopyResult.Failed:
                await _appLog.LogAsync(LogServiceId.OneDrive, "onedrive_error",
                    $"Копіювання {relativePath} (спроб: {job.Attempt + 1}): {error}");
                break;

            case CopyResult.NotConfigured when !_pathMissingLogged:
                // Once per config load, otherwise every file would spam the log
                _pathMissingLogged = true;
                await _appLog.LogAsync(LogServiceId.OneDrive, "onedrive_skip",
                    "Не задано шлях OneDrive (access_settings → onedrive → local_path)");
                break;
        }
    }

    private static async Task RequeueLaterAsync(Channel<CopyJob> queue, CopyJob job, TimeSpan delay, CancellationToken token)
    {
        try
        {
            await Task.Delay(delay, token);
            queue.Writer.TryWrite(job);
        }
        catch (OperationCanceledException) { }
    }

    internal static (CopyResult Result, string RelativePath, string? Error) CopyOnce(CopyJob job, string oneDriveRoot)
    {
        string relativePath = Path.GetRelativePath(job.StorageRoot, job.Source);
        if (string.IsNullOrWhiteSpace(oneDriveRoot))
            return (CopyResult.NotConfigured, relativePath, null);
        if (relativePath.StartsWith("..") || Path.IsPathRooted(relativePath))
            return (CopyResult.Failed, relativePath, "файл поза кореневою папкою");
        if (!File.Exists(job.Source))
            return (CopyResult.Failed, relativePath, "файл-джерело зник");

        string destPath = Path.Combine(oneDriveRoot, relativePath);
        try
        {
            if (IsUpToDate(job.Source, destPath))
                return (CopyResult.UpToDate, relativePath, null);

            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            File.Copy(job.Source, destPath, overwrite: true);
            // Copy keeps the device's LastWriteTime; cleanup counts days from arrival instead
            File.SetCreationTime(destPath, DateTime.Now);
            return (CopyResult.Copied, relativePath, null);
        }
        catch (IOException ex) when (ex is not FileNotFoundException and not DirectoryNotFoundException)
        {
            // Usually the OneDrive client holding the file while uploading
            return (CopyResult.Busy, relativePath, ex.Message);
        }
        catch (Exception ex)
        {
            return (CopyResult.Failed, relativePath, ex.Message);
        }
    }

    // File.Copy preserves size and LastWriteTime, so equal ones mean the same content
    private static bool IsUpToDate(string source, string dest)
    {
        var d = new FileInfo(dest);
        if (!d.Exists) return false;
        var s = new FileInfo(source);
        return s.Length == d.Length && s.LastWriteTimeUtc == d.LastWriteTimeUtc;
    }

    // ───────────── catch-up ─────────────

    private async Task CatchUpLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await CatchUpAsync(token);
                await Task.Delay(CatchUpInterval, token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                await _appLog.LogAsync(LogServiceId.OneDrive, "onedrive_error", $"Догонялка: {ex.Message}");
                await Task.Delay(TimeSpan.FromMinutes(5), token).ContinueWith(_ => { });
            }
        }
    }

    internal async Task<(int Checked, int Queued)> CatchUpAsync(CancellationToken token)
    {
        var config = await GetConfigAsync();
        string storageRoot = await _configRepository.GetRootFolderAsync();
        if (string.IsNullOrWhiteSpace(config.Path) || string.IsNullOrWhiteSpace(storageRoot) || config.Days <= 0)
            return (0, 0);

        var folders = await _fileDataRepository.GetObjectFoldersAsync();
        DateTime cutoff = DateTime.Now.AddDays(-config.Days);
        int checkedCount = 0, queued = 0;

        foreach (string objectFolder in folders)
        {
            token.ThrowIfCancellationRequested();

            string relative = Path.GetRelativePath(storageRoot, objectFolder);
            if (relative.StartsWith("..") || Path.IsPathRooted(relative)) continue;

            // Object folder must exist in OneDrive even without files: Properties shares it by path
            TryCreateDirectory(Path.Combine(config.Path, relative));

            if (!Directory.Exists(objectFolder)) continue;

            foreach (string monthDir in Directory.EnumerateDirectories(objectFolder))
            {
                if (!IsMonthInWindow(Path.GetFileName(monthDir), cutoff)) continue;

                foreach (string file in Directory.EnumerateFiles(monthDir))
                {
                    checkedCount++;
                    // LastWriteTime = event time; older ones were already cleaned up from OneDrive on purpose
                    if (File.GetLastWriteTime(file) < cutoff) continue;

                    string dest = Path.Combine(config.Path, Path.GetRelativePath(storageRoot, file));
                    if (IsUpToDate(file, dest)) continue;

                    Enqueue(file, storageRoot);
                    queued++;
                }
            }
        }

        await _appLog.LogAsync(LogServiceId.OneDrive, "onedrive_catchup",
            $"Об'єктів: {folders.Count}, перевірено файлів: {checkedCount}, поставлено в чергу: {queued}");
        return (checkedCount, queued);
    }

    // "2026_10" overlaps the window if the month ends after the cutoff
    internal static bool IsMonthInWindow(string folderName, DateTime cutoff)
    {
        var m = MonthFolder.Match(folderName);
        if (!m.Success) return false;
        int year = int.Parse(m.Groups[1].Value), month = int.Parse(m.Groups[2].Value);
        if (month is < 1 or > 12) return false;
        return new DateTime(year, month, 1).AddMonths(1) > cutoff;
    }

    private static void TryCreateDirectory(string path)
    {
        try { Directory.CreateDirectory(path); } catch { /* reported by the copy of the first file */ }
    }

    // ───────────── cleanup ─────────────

    private async Task CleanupLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(CalculateDelayToNextRun(0, 10), token);
                var config = await GetConfigAsync();
                int deleted = await CleanUpOldFilesAsync(config.Path, config.Days);
                await _appLog.LogAsync(LogServiceId.OneDrive, "onedrive_cleanup",
                    $"Видалено файлів старших за {config.Days} дн.: {deleted}");
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                await _appLog.LogAsync(LogServiceId.OneDrive, "onedrive_error", $"Очистка: {ex.Message}");
                await Task.Delay(TimeSpan.FromMinutes(5), token).ContinueWith(_ => { });
            }
        }
    }

    private static TimeSpan CalculateDelayToNextRun(int targetHour, int targetMinute)
    {
        DateTime now = DateTime.Now;
        DateTime nextRun = now.Date.AddHours(targetHour).AddMinutes(targetMinute);
        if (now >= nextRun) nextRun = nextRun.AddDays(1);
        return nextRun - now;
    }

    /// <summary>Deletes files that arrived in OneDrive more than <paramref name="daysToKeep"/> days ago. Folders stay.</summary>
    internal async Task<int> CleanUpOldFilesAsync(string rootPath, int daysToKeep)
    {
        if (string.IsNullOrEmpty(rootPath) || !Directory.Exists(rootPath) || daysToKeep <= 0) return 0;

        DateTime cutoff = DateTime.Now.AddDays(-daysToKeep);
        int deleted = 0;
        foreach (var file in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
            if (info.CreationTime >= cutoff) continue;
            try
            {
                info.Attributes = FileAttributes.Normal;
                info.Delete();
                deleted++;
            }
            catch (Exception ex)
            {
                await _appLog.LogAsync(LogServiceId.OneDrive, "onedrive_error", $"Видалення {info.Name}: {ex.Message}");
            }
        }
        // Folders are NOT deleted: Graph permissions are bound to the folder item, a re-created folder has none
        return deleted;
    }

    // ───────────── config ─────────────

    private async Task<OneDriveConfig> GetConfigAsync()
    {
        if (_config != null && DateTime.Now - _configLoadedAt < ConfigTtl)
            return _config;

        try
        {
            _config = await _configRepository.GetOneDriveConfigAsync() ?? new OneDriveConfig();
            _configLoadedAt = DateTime.Now;
            _pathMissingLogged = false;
        }
        catch (Exception ex)
        {
            await _appLog.LogAsync(LogServiceId.OneDrive, "onedrive_error", $"Завантаження конфігурації: {ex.Message}");
            _config ??= new OneDriveConfig();
        }
        return _config;
    }
}
