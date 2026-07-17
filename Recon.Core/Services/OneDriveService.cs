using Microsoft.Extensions.Logging;
using Recon.Core.Interfaces;
using Recon.Core.Interfaces.Repositories;
using System.IO;

namespace Recon.Core.Services;

public class OneDriveService : IOneDriveService
{
    private readonly ILogger<OneDriveService> _logger;
    private readonly IConfigRepository _configRepository;

    private string? _rootPath;
    private int _months;
    private CancellationTokenSource? _cts;

    public OneDriveService(ILogger<OneDriveService> logger, IConfigRepository configRepository)
    {
        _logger = logger;
        _configRepository = configRepository;
    }

    public void CopyToOneDrive(string localSourcePath, string relativePath)
    {
        EnsureConfig();
        if (string.IsNullOrEmpty(_rootPath)) return;

        try
        {
            string destPath = Path.Combine(_rootPath, relativePath);
            string? destDir = Path.GetDirectoryName(destPath);
            if (destDir != null && !Directory.Exists(destDir))
                Directory.CreateDirectory(destDir);

            File.Copy(localSourcePath, destPath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Помилка копіювання в OneDrive: {Path}", relativePath);
        }
    }

    public void StartCleanupScheduler()
    {
        if (_cts != null) return;
        EnsureConfig();
        _cts = new CancellationTokenSource();
        Task.Run(() => CleanupLoop(_cts.Token));
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
                CleanUpOldFiles(_months);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Планувальник очистки OneDrive зупинено.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Критична помилка в планувальнику OneDrive");
            await Task.Delay(TimeSpan.FromMinutes(5), CancellationToken.None);
        }
    }

    private void CleanUpOldFiles(int monthsToKeep)
    {
        if (string.IsNullOrEmpty(_rootPath) || !Directory.Exists(_rootPath)) return;

        DateTime cutoff = DateTime.Now.AddMonths(-monthsToKeep);
        try
        {
            foreach (var file in Directory.GetFiles(_rootPath, "*.*", SearchOption.AllDirectories))
            {
                var info = new FileInfo(file);
                if (info.LastWriteTime < cutoff)
                {
                    try { info.Delete(); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Не вдалося видалити {File}", info.Name); }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Помилка при очистці OneDrive");
        }
    }

    private static TimeSpan CalculateDelayToNextRun(int targetHour, int targetMinute)
    {
        var now = DateTime.Now;
        var nextRun = now.Date.AddHours(targetHour).AddMinutes(targetMinute);
        if (now >= nextRun) nextRun = nextRun.AddDays(1);
        return nextRun - now;
    }

    private void EnsureConfig()
    {
        if (_rootPath != null) return;
        // Called only from FTP background task (thread pool) — GetAwaiter().GetResult() is safe
        var config = _configRepository.GetOneDriveConfigAsync().GetAwaiter().GetResult();
        _rootPath = config.Path;
        _months = config.Months;
    }
}
