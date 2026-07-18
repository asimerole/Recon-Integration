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
        if (string.IsNullOrEmpty(_rootPath)) return;

        try
        {
            string destPath = Path.Combine(_rootPath, relativePath);
            string destDir = Path.GetDirectoryName(destPath);

            if (!Directory.Exists(destDir)) Directory.CreateDirectory(destDir);
            
            File.Copy(localSourcePath, destPath, true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Помилка копіювання в OneDrive: {Path}", relativePath);        
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
                _months = config.Months;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Помилка завантаження конфігурації OneDrive");
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
                TimeSpan delay = CalculateDelayToNextRun(0, 10); // Час: 0, Минуты: 10

                /*_logger.LogInformation("Наступна очистка запланована через {Time} (о {Date})", 
                    delay, DateTime.Now.Add(delay));*/
                
                await Task.Delay(delay, token);
                
                CleanUpOldFiles(_months);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Планувальник очистки зупинено.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Критична помилка в планувальнику OneDrive");
            await Task.Delay(TimeSpan.FromMinutes(5), CancellationToken.None);
        }
    }
    
    private TimeSpan CalculateDelayToNextRun(int targetHour, int targetMinute)
    {
        DateTime now = DateTime.Now;
        // Берем сегодняшнюю дату и ставим нужное время
        DateTime nextRun = now.Date.AddHours(targetHour).AddMinutes(targetMinute);

        // Если это время сегодня уже прошло, переносим на завтра
        if (now >= nextRun)
        {
            nextRun = nextRun.AddDays(1);
        }

        return nextRun - now;
    }

    private void CleanUpOldFiles(int monthsToKeep)
    {
        if (string.IsNullOrEmpty(_rootPath) || !Directory.Exists(_rootPath)) return;
        //_logger.LogInformation("Починаю очистку старих файлів OneDrive (старше {M} міс)...", monthsToKeep);
        DateTime cutoffDate = DateTime.Now.AddMonths(-monthsToKeep);
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
                        fileInfo.Delete();
                    }
                    catch (Exception delEx)
                    {
                        _logger.LogWarning("Не вдалося видалити старий файл {File}: {Msg}", fileInfo.Name, delEx.Message);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Помилка при очистці OneDrive");
        }
    }
}