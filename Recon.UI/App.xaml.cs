using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Recon.Core.Infrastructure;
using Recon.Core.Interfaces;
using Recon.Core.Interfaces.Repositories;
using Recon.Core.Repositories;
using Recon.Core.Services;
using Recon.UI.ViewModels;
using Serilog;
using Microsoft.Data.SqlClient;

namespace Recon.UI;

public partial class App : Application
{
    private IHost _host;
    private TaskbarIcon _notifyIcon;
    private TrayMenuWindow? _trayMenuWindow;

    public App()
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File("logs/general-.log", rollingInterval: RollingInterval.Day)
            .CreateLogger();

        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

        _host = Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureServices((_, services) =>
            {
                // Infrastructure
                services.AddSingleton<IDbConnectionFactory, SqlConnectionFactory>();

                // Repositories
                services.AddSingleton<IUserRepository, UserRepository>();
                services.AddSingleton<IConfigRepository, ConfigRepository>();
                services.AddSingleton<IFileDataRepository, FileDataRepository>();
                services.AddSingleton<IServerRepository, ServerRepository>();
                services.AddSingleton<IAppLogRepository, AppLogRepository>();

                // Services
                services.AddSingleton<IAuthService, AuthService>();
                services.AddSingleton<IFtpService, FtpService>();
                services.AddSingleton<IMailService, MailService>();
                services.AddSingleton<IIntegrationService, IntegrationService>();
                services.AddSingleton<IOneDriveService, OneDriveService>();
                services.AddSingleton<ICryptoService, CryptoService>();
                services.AddSingleton<IConfigService, ConfigService>();
                services.AddSingleton<ConfigMonitorService>();
                services.AddSingleton<IStatisticsService, StatisticsService>();
                services.AddSingleton<BrokenFileService>();

                // UI
                services.AddTransient<AuthWindow>();
                services.AddSingleton<TrayViewModel>();
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
                InitializeTrayIcon();
            else
            {
                await _host.StopAsync();
                Shutdown();
            }
        }
        catch (SqlException ex)
        {
            Log.Error(ex, "Критична помилка підключення до БД при запуску.");
            MessageBox.Show(
                $"Не вдалося підключитися до бази даних.\n\nПеревірте:\n1. Доступний сервер (VPN/Мережа).\n2. Відкритий порт.\n\nДеталі:\n{ex.Message}",
                "Помилка підключення", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Необроблений виняток при запуску.");
            MessageBox.Show($"Критична помилка при запуску:\n{ex}", "Помилка", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    private void InitializeTrayIcon()
    {
        _notifyIcon = (TaskbarIcon)FindResource("MyTrayIcon");
        var vm = _host.Services.GetRequiredService<TrayViewModel>();

        _trayMenuWindow = new TrayMenuWindow { DataContext = vm };
        _notifyIcon.TrayLeftMouseDown  += (_, _) => _trayMenuWindow.ShowAtTrayPosition();
        _notifyIcon.TrayRightMouseDown += (_, _) => _trayMenuWindow.ShowAtTrayPosition();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        _trayMenuWindow?.Close();
        _notifyIcon?.Dispose();
        await _host.StopAsync();
        _host.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
