using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Recon.Core.Enums;
using Recon.Core.Interfaces;
using Recon.Core.Models;

namespace Recon.UI.ViewModels;

public class StatsViewModel : ObservableObject
{
    private readonly IStatisticsService _statsService;
    private readonly DispatcherTimer _timer;

    public ObservableCollection<ServiceStatItem> StatItems { get; } = new()
    {
        new ServiceStatItem { ServiceName = "База Даних (SQL)" },
        new ServiceStatItem { ServiceName = "FTP Завантаження" },
        new ServiceStatItem { ServiceName = "OneDrive Хмара" },
        new ServiceStatItem { ServiceName = "Email Розсилка" }
    };

    public StatsViewModel(IStatisticsService statsService)
    {
        _statsService = statsService;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) => RefreshData();
        _timer.Start();

        RefreshData();
    }

    private void RefreshData()
    {
        UpdateItem(0, ServiceType.Integration);
        UpdateItem(1, ServiceType.Ftp);
        UpdateItem(2, ServiceType.OneDrive);
        UpdateItem(3, ServiceType.Mailing);
    }

    private void UpdateItem(int index, ServiceType type)
    {
        var stats = _statsService.GetStats(type);
        var item = StatItems[index];
        item.Last2Hours = stats.Last2Hours;
        item.Today = stats.Today;
    }
}
