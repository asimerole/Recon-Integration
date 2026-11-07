using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Recon.Core.Interfaces.Repositories;
using Recon.Core.Models;

namespace Recon.UI.ViewModels;

public partial class LogViewModel : ObservableObject
{
    private readonly IAppLogRepository _repo;
    private List<AppLogEntry> _allLogs = new();
    private readonly DispatcherTimer _refreshTimer;

    [ObservableProperty] private ObservableCollection<AppLogEntry> _logs = new();
    [ObservableProperty] private string  _searchText        = string.Empty;
    [ObservableProperty] private int     _selectedServiceId = 0;
    [ObservableProperty] private string  _statusText        = "Завантаження...";
    [ObservableProperty] private bool    _autoRefresh       = true;
    [ObservableProperty] private DateTime? _dateFrom;
    [ObservableProperty] private DateTime? _dateTo;

    public List<ServiceFilterOption> ServiceOptions { get; } = new()
    {
        new(0, "Всі модулі"),
        new(1, "FTP"),
        new(2, "Інтеграція"),
        new(3, "Пошта"),
        new(4, "OneDrive"),
    };

    public LogViewModel(IAppLogRepository repo)
    {
        _repo = repo;

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _refreshTimer.Tick += async (_, _) => await RefreshCoreAsync();
        _refreshTimer.Start();

        _ = RefreshCoreAsync();
    }

    partial void OnSearchTextChanged(string value)        => ApplyFilter();
    partial void OnSelectedServiceIdChanged(int value)    => ApplyFilter();
    partial void OnDateFromChanged(DateTime? value)       => ApplyFilter();
    partial void OnDateToChanged(DateTime? value)         => ApplyFilter();

    private bool _suspendFilter;

    [RelayCommand]
    private void ResetFilters()
    {
        // One ApplyFilter instead of four re-filters of 20k rows
        _suspendFilter = true;
        SearchText = string.Empty;
        SelectedServiceId = 0;
        DateFrom = null;
        DateTo = null;
        _suspendFilter = false;
        ApplyFilter();
    }
    partial void OnAutoRefreshChanged(bool value)
    {
        if (value) _refreshTimer.Start();
        else       _refreshTimer.Stop();
    }

    [RelayCommand]
    private async Task Refresh() => await RefreshCoreAsync();

    private async Task RefreshCoreAsync()
    {
        _allLogs = await _repo.GetLogsAsync(20000);
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (_suspendFilter) return;
        IEnumerable<AppLogEntry> view = _allLogs;

        if (SelectedServiceId > 0)
            view = view.Where(l => l.ServiceId == SelectedServiceId);

        // Date pickers give midnight; "по" includes the whole selected day
        if (DateFrom.HasValue)
            view = view.Where(l => l.CreatedAt >= DateFrom.Value.Date);
        if (DateTo.HasValue)
            view = view.Where(l => l.CreatedAt < DateTo.Value.Date.AddDays(1));

        // Every word must be found somewhere in the row: "353 error" → errors of registrator 353
        var words = SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 0)
            view = view.Where(l =>
            {
                string row = $"{l.CreatedAt:dd.MM.yy HH:mm:ss} {l.ServiceName} {l.ActionType} {l.Details}";
                return words.All(w => row.Contains(w, StringComparison.OrdinalIgnoreCase));
            });

        var list = view.ToList();
        Logs = new ObservableCollection<AppLogEntry>(list);
        StatusText = $"Показано: {list.Count:N0} з {_allLogs.Count:N0} записів";
    }

    [RelayCommand]
    private async Task CleanupLogs()
    {
        StatusText = "Очищення...";
        await _repo.CleanupOldLogsAsync(20000);
        await RefreshCoreAsync();
    }

    public void StopTimer() => _refreshTimer.Stop();
}

public record ServiceFilterOption(int Id, string Name)
{
    // The custom ComboBox template shows the selected item via ToString()
    public override string ToString() => Name;
}
