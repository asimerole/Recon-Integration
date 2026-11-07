using Recon.Core.Constants;
using Recon.Core.Interfaces;
using Recon.Core.Interfaces.Repositories;
using Recon.Core.Options;
using System.IO;

namespace Recon.Core.Services;

public class OneDriveService : IOneDriveService
{
    private static readonly TimeSpan ConfigTtl = TimeSpan.FromMinutes(1);

    private readonly IAppLogRepository _appLog;
    private readonly IConfigRepository _configRepository;

    private OneDriveConfig? _config;
    private DateTime _configLoadedAt;
    private bool _pathMissingLogged;

    private CancellationTokenSource? _cts;

    public OneDriveService(IAppLogRepository appLog, IConfigRepository configRepository)
    {
        _appLog = appLog;
        _configRepository = configRepository;
    }

    public async Task<bool> CopyFromStorageAsync(string sourceFullPath, string storageRoot)
    {
        var config = await GetConfigAsync();
        if (string.IsNullOrWhiteSpace(config.Path))
        {
            // Log once per config load, otherwise every file would spam the log
            if (!_pathMissingLogged)
            {
                _pathMissingLogged = true;
                await _appLog.LogAsync(LogServiceId.OneDrive, "onedrive_skip",
                    "Не задано шлях OneDrive (access_settings → onedrive → local_path)");
            }
            return false;
        }

        string relativePath = Path.GetRelativePath(storageRoot, sourceFullPath);
        if (relativePath.StartsWith("..") || Path.IsPathRooted(relativePath))
        {
            await _appLog.LogAsync(LogServiceId.OneDrive, "onedrive_skip",
                $"Файл поза кореневою папкою: {sourceFullPath}");
            return false;
        }

        try
        {
            string destPath = Path.Combine(config.Path, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

            File.Copy(sourceFullPath, destPath, overwrite: true);
            // Copy keeps the device's LastWriteTime; cleanup counts days from arrival instead
            File.SetCreationTime(destPath, DateTime.Now);

            await _appLog.LogAsync(LogServiceId.OneDrive, "onedrive_copied", relativePath);
            return true;
        }
        catch (Exception ex)
        {
            await _appLog.LogAsync(LogServiceId.OneDrive, "onedrive_error",
                $"Копіювання {relativePath}: {ex.Message}");
            return false;
        }
    }

    public void StartCleanupScheduler()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();

        var token = _cts.Token;
        Task.Run(() => CleanupLoop(token));
    }

    public void StopCleanupScheduler()
    {
        _cts?.Cancel();
        _cts = null;
    }

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

    private async Task CleanupLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(CalculateDelayToNextRun(0, 10), token);
                var config = await GetConfigAsync();
                await CleanUpOldFilesAsync(config.Path, config.Days);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                await _appLog.LogAsync(LogServiceId.OneDrive, "onedrive_error", $"Планувальник: {ex.Message}");
                await Task.Delay(TimeSpan.FromMinutes(5), CancellationToken.None);
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

    private async Task CleanUpOldFilesAsync(string rootPath, int daysToKeep)
    {
        if (string.IsNullOrEmpty(rootPath) || !Directory.Exists(rootPath) || daysToKeep <= 0) return;

        DateTime cutoffDate = DateTime.Now.AddDays(-daysToKeep);
        int deleted = 0;
        try
        {
            foreach (var file in Directory.GetFiles(rootPath, "*.*", SearchOption.AllDirectories))
            {
                var fileInfo = new FileInfo(file);
                if (fileInfo.CreationTime >= cutoffDate) continue;

                try
                {
                    fileInfo.Attributes = FileAttributes.Normal;
                    fileInfo.Delete();
                    deleted++;
                }
                catch (Exception delEx)
                {
                    await _appLog.LogAsync(LogServiceId.OneDrive, "onedrive_error", $"Видалення {fileInfo.Name}: {delEx.Message}");
                }
            }

            DeleteEmptyDirectories(rootPath);
            await _appLog.LogAsync(LogServiceId.OneDrive, "onedrive_cleanup", $"Видалено файлів старших за {daysToKeep} дн.: {deleted}");
        }
        catch (Exception ex)
        {
            await _appLog.LogAsync(LogServiceId.OneDrive, "onedrive_error", $"Очистка: {ex.Message}");
        }
    }

    private static void DeleteEmptyDirectories(string root)
    {
        foreach (var dir in Directory.GetDirectories(root, "*", SearchOption.AllDirectories)
                     .OrderByDescending(d => d.Length))
        {
            try
            {
                if (Directory.GetFiles(dir).Length == 0 && Directory.GetDirectories(dir).Length == 0)
                    Directory.Delete(dir);
            }
            catch { }
        }
    }
}
