using Recon.Core.Constants;
using Recon.Core.Interfaces;
using Recon.Core.Interfaces.Repositories;
using System.IO;

namespace Recon.Core.Services;

public class OneDriveService : IOneDriveService
{
    private readonly IAppLogRepository _appLog;
    private readonly IConfigRepository _configRepository;
    private string? _rootPath;
    private int _days;

    private CancellationTokenSource? _cts;

    public OneDriveService(IAppLogRepository appLog, IConfigRepository configRepository)
    {
        _appLog = appLog;
        _configRepository = configRepository;
    }

    public void CopyToOneDrive(string localSourcePath, string relativePath)
    {
        if (string.IsNullOrEmpty(_rootPath)) return;

        try
        {
            string destPath = Path.Combine(_rootPath, relativePath);
            string destDir = Path.GetDirectoryName(destPath)!;

            if (!Directory.Exists(destDir)) Directory.CreateDirectory(destDir);

            File.Copy(localSourcePath, destPath, true);
        }
        catch (Exception ex)
        {
            _ = _appLog.LogAsync(LogServiceId.OneDrive, "onedrive_error", $"Копіювання {relativePath}: {ex.Message}");
        }
    }

    public void StartCleanupScheduler()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();

        var token = _cts.Token;
        Task.Run(async () =>
        {
            try
            {
                var config = await _configRepository.GetOneDriveConfigAsync();
                _rootPath = config.Path;
                _days = config.Days;
            }
            catch (Exception ex)
            {
                await _appLog.LogAsync(LogServiceId.OneDrive, "onedrive_error", $"Завантаження конфігурації: {ex.Message}");
            }
            await CleanupLoop(token);
        });
    }

    public void StopCleanupScheduler()
    {
        _cts?.Cancel();
        _cts = null;
    }

    private async Task CleanupLoop(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                TimeSpan delay = CalculateDelayToNextRun(0, 10);
                await Task.Delay(delay, token);
                CleanUpOldFiles(_days);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await _appLog.LogAsync(LogServiceId.OneDrive, "onedrive_error", $"Планувальник: {ex.Message}");
            await Task.Delay(TimeSpan.FromMinutes(5), CancellationToken.None);
        }
    }

    private TimeSpan CalculateDelayToNextRun(int targetHour, int targetMinute)
    {
        DateTime now = DateTime.Now;
        DateTime nextRun = now.Date.AddHours(targetHour).AddMinutes(targetMinute);
        if (now >= nextRun) nextRun = nextRun.AddDays(1);
        return nextRun - now;
    }

    private void CleanUpOldFiles(int daysToKeep)
    {
        if (string.IsNullOrEmpty(_rootPath) || !Directory.Exists(_rootPath)) return;

        DateTime cutoffDate = DateTime.Now.AddDays(-daysToKeep);
        try
        {
            var files = Directory.GetFiles(_rootPath, "*.*", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                var fileInfo = new FileInfo(file);
                if (fileInfo.LastWriteTime < cutoffDate)
                {
                    try
                    {
                        fileInfo.Attributes = FileAttributes.Normal;
                        fileInfo.Delete();
                    }
                    catch (Exception delEx)
                    {
                        _ = _appLog.LogAsync(LogServiceId.OneDrive, "onedrive_error", $"Видалення {fileInfo.Name}: {delEx.Message}");
                    }
                }
            }

            DeleteEmptyDirectories(_rootPath);
        }
        catch (Exception ex)
        {
            _ = _appLog.LogAsync(LogServiceId.OneDrive, "onedrive_error", $"Очистка: {ex.Message}");
        }
    }

    private void DeleteEmptyDirectories(string root)
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
