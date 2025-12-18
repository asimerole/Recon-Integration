using Microsoft.Extensions.Logging;
using Recon.Core.Interfaces;
using Recon.Core.Options;

namespace Recon.Core.Services;

public class ConfigMonitorService
{
    private readonly IDatabaseService _dbService;
    private readonly ILogger<ConfigMonitorService> _logger;
    
    public event Action<ModuleConfig>? OnConfigChanged;

    private CancellationTokenSource? _cts;
    private ModuleConfig? _lastKnownConfig; 

    public ConfigMonitorService(IDatabaseService dbService, ILogger<ConfigMonitorService> logger)
    {
        _dbService = dbService;
        _logger = logger;
    }

    public void StartMonitoring()
    {
        if (_cts != null) return; 

        _cts = new CancellationTokenSource();
        // _logger.LogInformation("Моніторинг змін конфігурації запущено.");
        
        Task.Run(() => MonitoringLoop(_cts.Token));
    }

    public void StopMonitoring()
    {
        if (_cts != null)
        {
            _cts.Cancel();
            _cts = null;
            //_logger.LogInformation("Моніторинг змін конфігурації зупинено.");
        }
    }

    private async Task MonitoringLoop(CancellationToken token)
    {
        try 
        {
            _lastKnownConfig = _dbService.GetModuleConfig();
        }
        catch { /* Игнорируем ошибки первого запуска */ }

        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), token);
                
                var remoteConfig = _dbService.GetModuleConfig();
                
                if (HasConfigChanged(_lastKnownConfig, remoteConfig))
                {
                    //_logger.LogInformation("Виявлено зміну налаштувань у базі даних.");
                    
                    OnConfigChanged?.Invoke(remoteConfig);
                    
                    _lastKnownConfig = remoteConfig;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Помилка циклу моніторингу конфігурації");
                await Task.Delay(TimeSpan.FromSeconds(15), CancellationToken.None);
            }
        }
    }

    private bool HasConfigChanged(ModuleConfig? oldConfig, ModuleConfig newConfig)
    {
        if (oldConfig == null) return true; 
        
        if (oldConfig.IsFtpActive != newConfig.IsFtpActive) return true;
        if (oldConfig.IsIntegrationActive != newConfig.IsIntegrationActive) return true;
        if (oldConfig.IsMailActive != newConfig.IsMailActive) return true;
        if (oldConfig.IsOneDriveActive != newConfig.IsOneDriveActive) return true;
        if (oldConfig.DbIsFull != newConfig.DbIsFull) return true;
        
        return false;
    }
}