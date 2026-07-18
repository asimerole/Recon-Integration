using Microsoft.Extensions.Logging;
using Recon.Core.Interfaces.Repositories;
using Recon.Core.Options;

namespace Recon.Core.Services;

public class ConfigMonitorService
{
    private readonly IConfigRepository _configRepository;
    private readonly ILogger<ConfigMonitorService> _logger;

    public event Action<ModuleConfig>? OnConfigChanged;

    private CancellationTokenSource? _cts;
    private ModuleConfig? _lastKnownConfig;

    public ConfigMonitorService(IConfigRepository configRepository, ILogger<ConfigMonitorService> logger)
    {
        _configRepository = configRepository;
        _logger = logger;
    }

    public void StartMonitoring()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        Task.Run(() => MonitoringLoop(_cts.Token));
    }

    public void StopMonitoring()
    {
        _cts?.Cancel();
        _cts = null;
    }

    private async Task MonitoringLoop(CancellationToken token)
    {
        try { _lastKnownConfig = await _configRepository.GetModuleConfigAsync(); }
        catch { /* ignore startup errors */ }

        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), token);

                var remoteConfig = await _configRepository.GetModuleConfigAsync();

                if (HasConfigChanged(_lastKnownConfig, remoteConfig))
                {
                    OnConfigChanged?.Invoke(remoteConfig);
                    _lastKnownConfig = remoteConfig;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Помилка циклу моніторингу конфігурації");
                await Task.Delay(TimeSpan.FromSeconds(15), CancellationToken.None);
            }
        }
    }

    private static bool HasConfigChanged(ModuleConfig? old, ModuleConfig current)
    {
        if (old == null) return true;
        return old.IsFtpActive         != current.IsFtpActive
            || old.IsIntegrationActive != current.IsIntegrationActive
            || old.IsMailActive        != current.IsMailActive
            || old.IsOneDriveActive    != current.IsOneDriveActive
            || old.DbIsFull            != current.DbIsFull;
    }
}
