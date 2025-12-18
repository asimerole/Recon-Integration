using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Recon.Core.Enums;
using Recon.Core.Interfaces;
using Recon.Core.Options;
using Recon.Core.Services;


namespace Recon.UI.ViewModels;

public partial class TrayViewModel : ObservableObject
{
    private readonly IIntegrationService _integrationService;
    private readonly ConfigMonitorService _configMonitor;
    private readonly IFtpService _ftpService;
    private readonly IMailService _mailService;
    private readonly IOneDriveService _oneDriveService;
    private readonly IDatabaseService _dbService;
    private readonly IStatisticsService _stats;
    private DispatcherTimer _timer;
    
    private string _statsButtonContent;
    public string StatsButtonContent
    {
        get => _statsButtonContent;
        set { _statsButtonContent = value; OnPropertyChanged(); }
    }

    private bool _dbIsFull = false;
    private bool _fastDatabaseBuild = false;
    private bool _isInitializing = false;
    private bool _isSyncingWithDb = false;
    
    [ObservableProperty]
    private string _version;
    
    [ObservableProperty]
    private string _progress;
    
    [ObservableProperty]
    private bool _isFtpActive;
    
    [ObservableProperty]
    private bool _isIntegrationActive;
    
    [ObservableProperty]
    private bool _isMailActive;
    
    [ObservableProperty]
    private bool _isOneDriveActive;

    public TrayViewModel(IFtpService ftpService, 
        IMailService mailService, 
        IOneDriveService oneDriveService, 
        IIntegrationService integrationService,
        IDatabaseService dbService,
        ConfigMonitorService configMonitor,
        IStatisticsService stats)
    {
        _stats = stats;
        _ftpService = ftpService;
        _mailService = mailService;
        _oneDriveService = oneDriveService;
        _integrationService = integrationService;
        _dbService = dbService;
        
        _configMonitor = configMonitor;
        _configMonitor.OnConfigChanged += OnRemoteConfigReceived;
        _configMonitor.StartMonitoring();

        IsFtpActive = false;
        IsIntegrationActive = false;
        IsMailActive = false;
        IsOneDriveActive = false;
        Version = "v. 1.1.0 від 18.12.2025";
        _isInitializing = true;
        
        LoadModuleStates();
        
        _isInitializing = false;
        
        RunModules();
    }
    
    [RelayCommand]
    private async Task NotifyAll()
    {
        string message = ShowInputDialog("Введіть повідомлення для користувачів:");
        
        if (string.IsNullOrWhiteSpace(message)) return;
        
        var users = _dbService.GetActiveUserEmails();
        
        if (users.Count == 0)
        {
            MessageBox.Show("Немає активних користувачів для розсилки.");
            return;
        }
        
        await Task.Run(async () =>
        {
            await _mailService.SendToAllAsync(users, "Нове повідомленя від адміністратора", message);
        });
        
        MessageBox.Show("Повідомлення успішно розіслані!"); 
    }
    
    [RelayCommand]
    private void ExitApp()
    {
        Application.Current.Shutdown();
    }
    
    [RelayCommand]
    private async Task RebuildDatabase()
    {
        var wantRebuild = MessageBox.Show(
            "Ви впевнені, що хочете повністю очистити базу даних?\nВсі дані будуть втрачені.", 
            "Підтвердження", 
            MessageBoxButton.YesNo, 
            MessageBoxImage.Warning);
        
        if(wantRebuild == MessageBoxResult.No) return;
        
        var isFastReBuild = MessageBox.Show(
            "Використати прискорену збірку бази? (Тільки нові файли)", 
            "Спосіб перезбірки", 
            MessageBoxButton.YesNo, 
            MessageBoxImage.Question);

        _fastDatabaseBuild = isFastReBuild == MessageBoxResult.Yes;
        _integrationService.SetFastBuild(_fastDatabaseBuild);
        
        IsIntegrationActive = false;

        try 
        {
            await _dbService.RebuildDatabaseAsync();
            
            var config = _dbService.GetModuleConfig();
            config.DbIsFull = false;
            _dbService.SaveModuleConfig(config);
            
            var wantStartIntegration = MessageBox.Show(
                "Базу даних було успішно очищено.\nЗапустити модуль інтеграції файлів?",
                "Успіх", 
                MessageBoxButton.YesNo, 
                MessageBoxImage.Information);

            if (wantStartIntegration == MessageBoxResult.Yes)
            {
                IsIntegrationActive = true;
                _integrationService.SetFastBuild(_fastDatabaseBuild);
            } 
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Помилка: {ex.Message}", "Помилка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    
    // methods
    private void RunModules()
    {
        var progressReporter = CreateProgressReporter();
        progressReporter.Report(0);
        if (IsIntegrationActive) _integrationService.StartIntegration(progressReporter);
        if (IsFtpActive) _ftpService.StartFTP();
        
        _oneDriveService.StartCleanupScheduler();
        
    }
    
    private IProgress<int> CreateProgressReporter()
    {
        return new Progress<int>(percent => 
        {
            Progress = $"Інтеграція: {percent}%"; 
        });
    }

    private void LoadModuleStates()
    {
        var config = _dbService.GetModuleConfig();
        
        IsFtpActive = config.IsFtpActive;
        IsIntegrationActive = config.IsIntegrationActive;
        IsMailActive = config.IsMailActive;
        IsOneDriveActive = config.IsOneDriveActive;
        _dbIsFull = config.DbIsFull;
        _fastDatabaseBuild = config.FastDatabaseBuild;
    }

    private void SaveCurrentState()
    {
        var config = new ModuleConfig
        {
            IsFtpActive = IsFtpActive,
            IsIntegrationActive = IsIntegrationActive,
            IsMailActive = IsMailActive,
            IsOneDriveActive = IsOneDriveActive,
            FastDatabaseBuild = _fastDatabaseBuild,
            DbIsFull = _dbIsFull
        };

        _dbService.SaveModuleConfig(config);
    }
    
    private string ShowInputDialog(string prompt)
    {
        var inputWindow = new InputWindow(prompt);
        
        bool? result = inputWindow.ShowDialog();

        if (result == true)
        {
            return inputWindow.ResponseText;
        }

        return string.Empty; 
    }
    
    private void OnRemoteConfigReceived(ModuleConfig remoteConfig)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            _isSyncingWithDb = true;
            
            if (IsFtpActive != remoteConfig.IsFtpActive)
                IsFtpActive = remoteConfig.IsFtpActive;

            if (IsIntegrationActive != remoteConfig.IsIntegrationActive)
                IsIntegrationActive = remoteConfig.IsIntegrationActive;

            if (IsMailActive != remoteConfig.IsMailActive)
                IsMailActive = remoteConfig.IsMailActive;
                
            if (IsOneDriveActive != remoteConfig.IsOneDriveActive)
                IsOneDriveActive = remoteConfig.IsOneDriveActive;
            
            _isSyncingWithDb = false;
        });
    }

    partial void OnIsFtpActiveChanged(bool value)
    {
        if (_isSyncingWithDb || _isInitializing) return;
        SaveCurrentState();

        if (value)
        {
            _ftpService.StartFTP();
        }
        else
        {
            _ftpService.StopFTP();
        }
    }

    partial void OnIsIntegrationActiveChanged(bool value)
    {
        if (_isSyncingWithDb || _isInitializing) return;
        SaveCurrentState();

        if (value)
        {
            var progressReporter = CreateProgressReporter();
            progressReporter.Report(0);
            _integrationService.StartIntegration(progressReporter);
        }
        else
        {
            _integrationService.StopIntegration();
        }
    }

    partial void OnIsMailActiveChanged(bool value)
    {
        if (_isSyncingWithDb || _isInitializing) return;
        
        SaveCurrentState();
    }

    partial void OnIsOneDriveActiveChanged(bool value)
    {
        if (_isSyncingWithDb || _isInitializing) return;
        
        SaveCurrentState();
    }
    
}