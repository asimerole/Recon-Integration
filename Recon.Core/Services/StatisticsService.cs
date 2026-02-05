using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;
using Recon.Core.Enums;
using Recon.Core.Interfaces;
using Recon.Core.Options;
using Microsoft.Extensions.Logging;

namespace Recon.Core.Services;

public class StatisticsService : IStatisticsService
{
    // Dictionary: Key = Service, Value = List of event times
    private readonly ConcurrentDictionary<ServiceType, List<DateTime>> _history 
        = new ConcurrentDictionary<ServiceType, List<DateTime>>();

    // Separate “Per day” meter (cumulative)
    private readonly ConcurrentDictionary<ServiceType, int> _dailyCounters 
        = new ConcurrentDictionary<ServiceType, int>();

    private readonly object _lock = new object();

    private readonly IDatabaseService _databaseService;
    private readonly ILogger<OneDrivePermissonService> _logger;
    public StatisticsService(IDatabaseService databaseService)
    {
        _databaseService = databaseService;
        
        // Initialize empty lists
        foreach (ServiceType type in Enum.GetValues(typeof(ServiceType)))
        {
            _history[type] = new List<DateTime>();
            _dailyCounters[type] = 0;
        }
    }

    public void RegisterAction(ServiceType type, int count = 1)
    {
        lock (_lock)
        {
            var now = DateTime.Now;
            
            // 1. Add to history (for 2 hours)
            var list = _history[type];
            for (int i = 0; i < count; i++) list.Add(now);

            // 2. Add “Per day”
            _dailyCounters[type] += count;
        }
    }

    // This method is needed to “pull” the actual figure from the database for integration.
    public void SetDailyCountFromDb(ServiceType type, int dbCount)
    {
        _dailyCounters[type] = dbCount;
    }

    public (int Last2Hours, int Today) GetStats(ServiceType type)
    {
        lock (_lock)
        {
            var list = _history[type];
            var cutOff = DateTime.Now.AddHours(-2);

            // Deleting old records (2-hour window)
            list.RemoveAll(t => t < cutOff);

            return (list.Count, _dailyCounters[type]);
        }
    }
    
    public async Task<bool> SendAnalyticsToUsers(MailServerConfig config)
    {
        bool allSuccessed = true;
        try
        {
            
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, ex.Message);
        }

        return false;
    }
}