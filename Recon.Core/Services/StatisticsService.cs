using System.Collections.Concurrent;
using Recon.Core.Enums;
using Recon.Core.Interfaces;
using Recon.Core.Models;
using Recon.Core.Options;
using Microsoft.Extensions.Logging;

namespace Recon.Core.Services;

public class StatisticsService : IStatisticsService
{
    private readonly ConcurrentDictionary<ServiceType, List<DateTime>> _history = new();
    private readonly ConcurrentDictionary<ServiceType, int> _dailyCounters = new();
    private readonly object _lock = new();
    private readonly ILogger<StatisticsService> _logger;

    public StatisticsService(ILogger<StatisticsService> logger)
    {
        _logger = logger;
        foreach (ServiceType type in Enum.GetValues(typeof(ServiceType)))
        {
            _history[type] = [];
            _dailyCounters[type] = 0;
        }
    }

    public void RegisterAction(ServiceType type, int count = 1)
    {
        lock (_lock)
        {
            var now = DateTime.Now;
            var list = _history[type];
            for (int i = 0; i < count; i++) list.Add(now);
            _dailyCounters[type] += count;
        }
    }

    public void SetDailyCountFromDb(ServiceType type, int dbCount) =>
        _dailyCounters[type] = dbCount;

    public (int Last2Hours, int Today) GetStats(ServiceType type)
    {
        lock (_lock)
        {
            var list = _history[type];
            list.RemoveAll(t => t < DateTime.Now.AddHours(-2));
            return (list.Count, _dailyCounters[type]);
        }
    }

    public string CheckDailyFileByServer(ServerInfo server)
    {
        var now = DateTime.Now;
        var last = server.LastDailyFileDate;

        if (last < now.AddDays(-3)) return $"проблеми з регістратором №{server.ReconId} (DAILY файлу не було більше 2 днів).";
        if (last < now.AddDays(-2)) return $"проблеми з регістратором №{server.ReconId} (файлу DAILY нема вже 2 дні).";
        if (last < now.AddDays(-1)) return "TotalFailure";
        return string.Empty;
    }

    public Dictionary<string, ErrorDetails> GetUnreachableServers(List<ServerInfo> servers) => [];

    public async Task<bool> SendAnalyticsToUsers(MailServerConfig config)
    {
        try { }
        catch (Exception ex) { _logger.LogError(ex, ex.Message); }
        return false;
    }
}
