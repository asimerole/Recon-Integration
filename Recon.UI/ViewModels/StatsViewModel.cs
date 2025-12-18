using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Threading;
using Recon.Core.Enums;
using Recon.Core.Interfaces;
using Recon.Core.Models;

namespace Recon.UI.ViewModels;

public class StatsViewModel 
{
    private readonly IStatisticsService _statsService;
    private readonly IDatabaseService _dbService;
    private DispatcherTimer _timer;

    // Колекція для прив'язки до DataGrid
    public ObservableCollection<ServiceStatItem> StatItems { get; set; }

    public StatsViewModel(IStatisticsService statsService, IDatabaseService dbService)
    {
        _statsService = statsService;
        _dbService = dbService;

        // Ініціалізуємо список (порядок рядків фіксований)
        StatItems = new ObservableCollection<ServiceStatItem>
        {
            new ServiceStatItem { ServiceName = "База Даних (SQL)" },
            new ServiceStatItem { ServiceName = "FTP Завантаження" },
            new ServiceStatItem { ServiceName = "OneDrive Хмара" },
            new ServiceStatItem { ServiceName = "Email Розсилка" }
        };

        // Таймер оновлення (раз на 3 сек)
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += async (s, e) => await RefreshData();
        _timer.Start();
        
        // Перший запуск одразу
        RefreshData();
    }

    private async Task RefreshData()
    {
        // 1. Оновлюємо "День" для бази (найважливіше)
        /*try 
        {
            int dbCount = await _dbService.GetTodayCountFromDbAsync();
            _statsService.SetDailyCountFromDb(ServiceType.Integration, dbCount);
        } 
        catch { /* Ігноруємо помилки мережі #1# }*/

        // 2. Отримуємо дані і оновлюємо рядки в колекції
        UpdateItem(0, ServiceType.Integration);
        UpdateItem(1, ServiceType.Ftp);
        UpdateItem(2, ServiceType.OneDrive);
        UpdateItem(3, ServiceType.Mailing);
    }

    private void UpdateItem(int index, ServiceType type)
    {
        var stats = _statsService.GetStats(type);
        
        // Update the properties of the existing object so that the table does not flicker.
        // (To do this, ServiceStatItem would also have to implement INotifyPropertyChanged, 
        // but for simplicity, you can simply replace the object if it has changed, or use Fody).
        
        // Simple option (without INotifyPropertyChanged inside Item):
        StatItems[index] = new ServiceStatItem 
        { 
            ServiceName = StatItems[index].ServiceName,
            Last2Hours = stats.Last2Hours,
            Today = stats.Today
        };
    }
    
    // ... реалізація INotifyPropertyChanged ...
}