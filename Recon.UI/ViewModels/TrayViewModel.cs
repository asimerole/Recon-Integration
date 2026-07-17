using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Recon.Core.Interfaces;
using Recon.Core.Interfaces.Repositories;
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
    private readonly IConfigRepository _configRepository;
    private readonly IUserRepository _userRepository;
    private readonly IFileDataRepository _fileDataRepository;
    private readonly IStatisticsService _stats;
    private readonly OneDrivePermissonService _oneDrivePermissionService;

    private DateTime _appStartTime;
    private DispatcherTimer _uptimeTimer = null!;
    private bool _fastDatabaseBuild;
    private bool _isInitializing;
    private bool _isSyncingWithDb;

    [ObservableProperty] private string _version = string.Empty;
    [ObservableProperty] private string _progress = string.Empty;
    [ObservableProperty] private bool _isFtpActive;
    [ObservableProperty] private bool _isIntegrationActive;
    [ObservableProperty] private bool _isMailActive;
    [ObservableProperty] private bool _isOneDriveActive;

    private string _uptimeStr = string.Empty;
    public string UptimeStr
    {
        get => _uptimeStr;
        set { _uptimeStr = value; OnPropertyChanged(); }
    }

    public TrayViewModel(IFtpService ftpService, IMailService mailService, IOneDriveService oneDriveService,
        IIntegrationService integrationService, IConfigRepository configRepository,
        IUserRepository userRepository, IFileDataRepository fileDataRepository,
        ConfigMonitorService configMonitor, IStatisticsService stats,
        OneDrivePermissonService oneDrivePermissonService)
    {
        _ftpService = ftpService;
        _mailService = mailService;
        _oneDriveService = oneDriveService;
        _integrationService = integrationService;
        _configRepository = configRepository;
        _userRepository = userRepository;
        _fileDataRepository = fileDataRepository;
        _configMonitor = configMonitor;
        _stats = stats;
        _oneDrivePermissionService = oneDrivePermissonService;

        _configMonitor.OnConfigChanged += OnRemoteConfigReceived;
        _configMonitor.StartMonitoring();

        Version = "v. 1.2.0";
        _isInitializing = true;
        _appStartTime = DateTime.Now;

        _uptimeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _uptimeTimer.Tick += (_, _) => UpdateUptime();
        _uptimeTimer.Start();
        UpdateUptime();

        LoadModuleStates();
        _isInitializing = false;
        RunModules();
    }

    private void UpdateUptime()
    {
        var now = DateTime.Now;
        var calc = _appStartTime;
        int months = 0;
        while (calc.AddMonths(1) <= now) { months++; calc = calc.AddMonths(1); }
        var diff = now - calc;
        UptimeStr = $"Час роботи: {months} міс. {diff.Days} дн. {diff.Hours} год. {diff.Minutes} хв. {diff.Seconds} сек.";
    }

    [RelayCommand]
    private async Task NotifyAll()
    {
        string message = ShowInputDialog("Введіть повідомлення для користувачів:");
        if (string.IsNullOrWhiteSpace(message)) return;

        var users = _userRepository.GetAllUserEmails();
        if (users.Count == 0)
        {
            MessageBox.Show("Немає активних користувачів для розсилки.");
            return;
        }

        await _mailService.SendToAllAsync(users, "Нове повідомлення від адміністратора", message);
        MessageBox.Show("Повідомлення успішно розіслані!");
    }

    [RelayCommand]
    private void ExitApp() => Application.Current.Shutdown();

    [RelayCommand]
    private async Task RebuildDatabase()
    {
        var confirm = MessageBox.Show(
            "Ви впевнені, що хочете повністю очистити базу даних?\nВсі дані будуть втрачені.",
            "Підтвердження", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm == MessageBoxResult.No) return;

        var fastBuildAnswer = MessageBox.Show(
            "Використати прискорену збірку бази? (Тільки нові файли)",
            "Спосіб перезбірки", MessageBoxButton.YesNo, MessageBoxImage.Question);
        _fastDatabaseBuild = fastBuildAnswer == MessageBoxResult.Yes;
        _integrationService.SetFastBuild(_fastDatabaseBuild);

        IsIntegrationActive = false;
        await _integrationService.StopIntegration();

        try
        {
            await _fileDataRepository.RebuildDatabaseAsync();

            var config = _configRepository.GetModuleConfig();
            config.DbIsFull = false;
            _configRepository.SaveModuleConfig(config);

            var start = MessageBox.Show(
                "Базу даних успішно очищено.\nЗапустити модуль інтеграції файлів?",
                "Успіх", MessageBoxButton.YesNo, MessageBoxImage.Information);

            if (start == MessageBoxResult.Yes)
            {
                _integrationService.SetFastBuild(_fastDatabaseBuild);
                _integrationService.StartIntegration(CreateProgressReporter());
                IsIntegrationActive = true;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Помилка: {ex.Message}", "Помилка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RunModules()
    {
        var progressReporter = CreateProgressReporter();
        progressReporter.Report(0);

        if (IsIntegrationActive) _integrationService.StartIntegration(progressReporter);
        if (IsFtpActive) _ftpService.StartFTP();

        _oneDriveService.StartCleanupScheduler();
        _mailService.StartSendingLoop();
        _oneDrivePermissionService.StartMonitoring();
    }

    private IProgress<int> CreateProgressReporter() =>
        new Progress<int>(percent => Progress = $"Інтеграція: {percent}%");

    private void LoadModuleStates()
    {
        var config = _configRepository.GetModuleConfig();
        IsFtpActive = config.IsFtpActive;
        IsIntegrationActive = config.IsIntegrationActive;
        IsMailActive = config.IsMailActive;
        IsOneDriveActive = config.IsOneDriveActive;
        _fastDatabaseBuild = config.FastDatabaseBuild;
    }

    private void SaveCurrentState()
    {
        _configRepository.SaveModuleConfig(new ModuleConfig
        {
            IsFtpActive = IsFtpActive,
            IsIntegrationActive = IsIntegrationActive,
            IsMailActive = IsMailActive,
            IsOneDriveActive = IsOneDriveActive,
            FastDatabaseBuild = _fastDatabaseBuild,
            DbIsFull = _configRepository.GetModuleConfig().DbIsFull
        });
    }

    private string ShowInputDialog(string prompt)
    {
        var inputWindow = new InputWindow(prompt);
        return inputWindow.ShowDialog() == true ? inputWindow.ResponseText : string.Empty;
    }

    private void OnRemoteConfigReceived(ModuleConfig remoteConfig)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            _isSyncingWithDb = true;
            if (IsFtpActive != remoteConfig.IsFtpActive) IsFtpActive = remoteConfig.IsFtpActive;
            if (IsIntegrationActive != remoteConfig.IsIntegrationActive) IsIntegrationActive = remoteConfig.IsIntegrationActive;
            if (IsMailActive != remoteConfig.IsMailActive) IsMailActive = remoteConfig.IsMailActive;
            if (IsOneDriveActive != remoteConfig.IsOneDriveActive) IsOneDriveActive = remoteConfig.IsOneDriveActive;
            _isSyncingWithDb = false;
        });
    }

    partial void OnIsFtpActiveChanged(bool value)
    {
        if (_isSyncingWithDb || _isInitializing) return;
        SaveCurrentState();
        if (value) _ftpService.StartFTP();
        else _ftpService.StopFTP();
    }

    partial void OnIsIntegrationActiveChanged(bool value)
    {
        if (_isSyncingWithDb || _isInitializing) return;
        SaveCurrentState();
        if (value)
        {
            var reporter = CreateProgressReporter();
            reporter.Report(0);
            _integrationService.StartIntegration(reporter);
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
