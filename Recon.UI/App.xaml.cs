using System.Configuration;
using System.Data;
using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Recon.Core.Interfaces;
using Recon.Core.Services;
using Recon.UI.ViewModels;
using Serilog;
using Serilog.Events;
using Serilog.Filters;
using TrayViewModel = Recon.UI.ViewModels.TrayViewModel;

namespace Recon.UI;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private IHost _host;
    private TaskbarIcon _notifyIcon;

    public App()
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            
            // --- Фильтр для FTP ---
            .WriteTo.Logger(l => l
                .Filter.ByIncludingOnly(Matching.FromSource<FtpService>()) 
                .WriteTo.File("logs/ftp-.log", rollingInterval: RollingInterval.Day))

            // --- Фильтр для Почты ---
            .WriteTo.Logger(l => l
                .Filter.ByIncludingOnly(Matching.FromSource<MailService>())
                .WriteTo.File("logs/mail-.log", rollingInterval: RollingInterval.Day))

            // --- Фильтр для БД ---
            .WriteTo.Logger(l => l
                .Filter.ByIncludingOnly(Matching.FromSource<DatabaseService>())
                .WriteTo.File("logs/database-.log", rollingInterval: RollingInterval.Day))
            
            // --- Фильтр для Интеграции ---
            .WriteTo.Logger(l => l
                .Filter.ByIncludingOnly(Matching.FromSource<IntegrationService>())
                .WriteTo.File("logs/integration-.log", rollingInterval: RollingInterval.Day))
            
            // --- Фильтр для OneDrive ---
            .WriteTo.Logger(l => l
                .Filter.ByIncludingOnly(Matching.FromSource<OneDriveService>())
                .WriteTo.File("logs/onedrive-.log", rollingInterval: RollingInterval.Day))

            // --- Общий лог (всё остальное + системные ошибки) ---
            // Исключаем то, что уже записали в спец. файлы, чтобы не дублировать (опционально)
            .WriteTo.File("logs/general-.log", rollingInterval: RollingInterval.Day)
            .CreateLogger();
        
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        
        _host = Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureServices((context, services) =>
            {
                // Твои сервисы (Core)
                services.AddSingleton<IAuthService, AuthService>();
                services.AddSingleton<IFtpService, FtpService>();
                services.AddSingleton<IMailService, MailService>();
                services.AddSingleton<IIntegrationService, IntegrationService>();
                services.AddSingleton<IOneDriveService, OneDriveService>();
                services.AddSingleton<ICryptoService, CryptoService>();
                services.AddSingleton<IDatabaseService, DatabaseService>();
                services.AddSingleton<IConfigService, ConfigService>();
                services.AddSingleton<ConfigMonitorService>();
                services.AddSingleton<IStatisticsService, StatisticsService>();
                services.AddSingleton<OneDrivePermissonService>();
                
                // Окна (View)
                services.AddTransient<AuthWindow>();        // WPF Window
                services.AddSingleton<TrayViewModel>();     // ViewModels logic
            })
            .Build();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        try
        {
            await _host.StartAsync();
            
            var authWindow = _host.Services.GetRequiredService<AuthWindow>();
            bool? result = authWindow.ShowDialog();

            if (result == true)
            {
                InitializeTrayIcon();
            }
            else
            {
                await _host.StopAsync();
                Shutdown();
            }
        }
        catch (Microsoft.Data.SqlClient.SqlException ex)
        {
            Log.Error(ex, "Критическая ошибка подключения к БД при запуске.");
            
            MessageBox.Show(
                $"Не удалось подключиться к базе данных.\n\nПроверьте:\n1. Доступен ли сервер (VPN/Сеть).\n2. Открыт ли порт.\n\nДетали ошибки:\n{ex.Message}", 
                "Ошибка подключения", 
                MessageBoxButton.OK, 
                MessageBoxImage.Error);
            
            Shutdown();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Необработанное исключение при запуске приложения.");
        
            MessageBox.Show(
                $"Критическая ошибка при запуске:\n{ex.Message}", 
                "Ошибка приложения", 
                MessageBoxButton.OK, 
                MessageBoxImage.Error);

            Shutdown();
        }
    }
    
    private void InitializeTrayIcon()
    {
        _notifyIcon = (TaskbarIcon)FindResource("MyTrayIcon");
        _notifyIcon.DataContext = _host.Services.GetRequiredService<TrayViewModel>();
        
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        _notifyIcon?.Dispose(); 
        
        await _host.StopAsync();
        _host.Dispose();
        Log.CloseAndFlush();
        
        base.OnExit(e);
    }
}