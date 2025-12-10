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

            // --- Общий лог (всё остальное + системные ошибки) ---
            // Исключаем то, что уже записали в спец. файлы, чтобы не дублировать (опционально)
            .WriteTo.File("logs/general-.log", rollingInterval: RollingInterval.Day)
            .CreateLogger();
        
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
                
                // Твои Окна (View)
                services.AddTransient<AuthWindow>();        // WPF Window
                services.AddSingleton<TrayViewModel>();     // ViewModels logic
            })
            .Build();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        await _host.StartAsync();

        // Показываем окно авторизации через DI
        var authWindow = _host.Services.GetRequiredService<AuthWindow>();
        bool? result = authWindow.ShowDialog();

        if (result == true)
        {
            
            // Если вошли — инициализируем трей (через ViewModel или сервис)
            InitializeTrayIcon();
        }
        else
        {
            await _host.StopAsync(); 
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