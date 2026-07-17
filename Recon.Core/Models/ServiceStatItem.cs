using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Recon.Core.Models;

public class ServiceStatItem : INotifyPropertyChanged
{
    private int _last2Hours;
    private int _today;
    private string _status = "OK";

    public string ServiceName { get; set; } = string.Empty;

    public int Last2Hours
    {
        get => _last2Hours;
        set { if (_last2Hours != value) { _last2Hours = value; OnPropertyChanged(); } }
    }

    public int Today
    {
        get => _today;
        set { if (_today != value) { _today = value; OnPropertyChanged(); } }
    }

    public string Status
    {
        get => _status;
        set { if (_status != value) { _status = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
