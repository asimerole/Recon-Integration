using FluentFTP;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Recon.Core.Enums;
using Recon.Core.Interfaces;
using Recon.Core.Interfaces.Repositories;
using Recon.Core.Models;
using System.IO;

namespace Recon.Core.Services;

public class FtpService : IFtpService
{
    private readonly IServerRepository _serverRepository;
    private readonly IConfigRepository _configRepository;
    private readonly ILogger<FtpService> _logger;
    private readonly IOneDriveService _oneDriveService;
    private readonly IStatisticsService _statsService;

    private string _ftpCacheDir = string.Empty;

    private CancellationTokenSource? _cts;
    private Task? _workingTask;

    public FtpService(IServerRepository serverRepository, IConfigRepository configRepository,
        ILogger<FtpService> logger, IOneDriveService oneDriveService, IStatisticsService statsService)
    {
        _serverRepository = serverRepository;
        _configRepository = configRepository;
        _logger = logger;
        _oneDriveService = oneDriveService;
        _statsService = statsService;
    }

    public void StartFTP()
    {
        if (_workingTask != null && !_workingTask.IsCompleted) return;

        _cts = new CancellationTokenSource();
        _workingTask = Task.Run(() => WorkerLoop(_cts.Token));
    }

    public void StopFTP()
    {
        if (_cts != null)
        {
            _cts.Cancel();
            _cts = null;
        }
    }

    private async Task WorkerLoop(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var rootFolder = _configRepository.GetRootFolder();
                var servers = _serverRepository.GetAllServers();

                _ftpCacheDir = Path.Combine(rootFolder, "Cache");
                if (!Directory.Exists(_ftpCacheDir))
                    Directory.CreateDirectory(_ftpCacheDir);

                foreach (var server in servers)
                {
                    if (token.IsCancellationRequested) break;

                    var config = _configRepository.GetModuleConfig();
                    if (!config.IsFtpActive) break;

                    bool isOneDriveActive = config.IsOneDriveActive;
                    var oneDriveConfig = _configRepository.GetOneDriveConfig();

                    CreateLocalDirectoryTree(server, rootFolder);
                    if (isOneDriveActive) CreateLocalDirectoryTree(server, oneDriveConfig.Path);

                    await ProcessServerAsync(server, isOneDriveActive, token);
                }

                int feedingTime = _configRepository.GetFeedingTime();
                await Task.Delay(TimeSpan.FromSeconds(feedingTime), token);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("FTP Service остановлен пользователем.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Критическая ошибка в цикле FTP");
            await Task.Delay(5000, CancellationToken.None);
        }
    }

    private async Task ProcessServerAsync(ServerInfo server, bool isOneDriveActive, CancellationToken token)
    {
        try
        {
            using var client = new AsyncFtpClient(server.IpAddress, server.Login, server.Password);
            ConfigureClient(client);

            await client.Connect(token);
            server.LastPingTime = DateTime.Now;

            var items = await client.GetListing(server.RemoteFolderPath, token);
            if (items.Length == 0) return;

            foreach (var item in items)
            {
                if (token.IsCancellationRequested) break;
                if (!StartsWithValidPrefix(item.Name)) continue;

                await ProcessSingleFileAsync(client, item, server, isOneDriveActive);
            }

            await client.Disconnect();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Критична помилка FTP на сервері {Ip}", server.IpAddress);
        }
    }

    private async Task ProcessSingleFileAsync(AsyncFtpClient client, FtpListItem item, ServerInfo server, bool isOneDriveActive)
    {
        try
        {
            if (!client.IsConnected) await client.Connect();

            string finalLocalPath = Path.Combine(_ftpCacheDir, item.Name);
            string tempLocalPath = finalLocalPath + ".tmp";

            var status = await client.DownloadFile(tempLocalPath, item.FullName, FtpLocalExists.Overwrite);

            if (status != FtpStatus.Success) return;

            await _serverRepository.UpdateDailyStatAsync(server.Id, "collected");
            await HandleDownloadedFileAsync(tempLocalPath, item, server);
            await client.DeleteFile(item.FullName);

            bool isRecon = item.Name.StartsWith("RECON", StringComparison.OrdinalIgnoreCase)
                        || item.Name.StartsWith("REXPR", StringComparison.OrdinalIgnoreCase);
            bool isDaily = item.Name.StartsWith("DAILY", StringComparison.OrdinalIgnoreCase);

            DateTime? updateDailyTime = null;
            if (isDaily)
            {
                updateDailyTime = item.Modified > DateTime.MinValue ? item.Modified : DateTime.Now;
                if (updateDailyTime.Value > server.LastDailyFileDate)
                    server.LastDailyFileDate = updateDailyTime.Value;
            }

            await _serverRepository.UpdateServerStatusAsync(
                server.StructId,
                lastPing: server.LastPingTime,
                lastRecon: isRecon ? DateTime.Now : null,
                lastDaily: updateDailyTime);

            _statsService.RegisterAction(ServiceType.Ftp);

            if (isOneDriveActive)
            {
                try
                {
                    var relativePath = Path.Combine(GetServerDirTree(server), item.Name);
                    _oneDriveService.CopyToOneDrive(tempLocalPath, relativePath);
                    await _serverRepository.UpdateDailyStatAsync(server.Id, "uploaded");
                    _statsService.RegisterAction(ServiceType.OneDrive);
                }
                catch (IOException ioEx)
                {
                    _logger.LogWarning("OneDrive зайнятий: {Msg}", ioEx.Message);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "OneDrive Error");
                }
            }

            try
            {
                if (File.Exists(finalLocalPath)) File.Delete(finalLocalPath);
                File.Move(tempLocalPath, finalLocalPath);
                if (item.Modified > DateTime.MinValue)
                    File.SetLastWriteTime(finalLocalPath, item.Modified);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Не удалось переименовать временный файл");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Помилка файлу {Name}", item.Name);
            await client.Disconnect();
        }
    }

    private async Task HandleDownloadedFileAsync(string localPath, FtpListItem item, ServerInfo server)
    {
        if (item.Modified > DateTime.MinValue)
        {
            try { File.SetLastWriteTime(localPath, item.Modified); }
            catch (Exception ex) { _logger.LogWarning("Дата файлу не змінена: {Msg}", ex.Message); }
        }

        if (item.Name.StartsWith("DAILY", StringComparison.OrdinalIgnoreCase) && item.Modified > server.LastDailyFileDate)
            server.LastDailyFileDate = item.Modified;

        await CreateMetaFileAsync(item.Name, server.LocalFolderPath, server.Id);
    }

    private async Task CreateMetaFileAsync(string fileName, string target, int serverId)
    {
        var metaData = new { targetPath = target, serverId };
        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        string json = JsonSerializer.Serialize(metaData, jsonOptions);
        string metaFilePath = Path.Combine(_ftpCacheDir, fileName + ".meta");

        try { await File.WriteAllTextAsync(metaFilePath, json); }
        catch (Exception ex) { _logger.LogWarning("Мета-файл не створено: {Msg}", ex.Message); }
    }

    private string GetServerDirTree(ServerInfo server)
    {
        string path = string.Empty;
        foreach (var part in server.Unit.Split('-', StringSplitOptions.RemoveEmptyEntries))
            path = Path.Combine(path, SanitizeFileName(part));
        path = Path.Combine(path, SanitizeFileName(server.Substation));
        path = Path.Combine(path, SanitizeFileName(server.Object));
        return path;
    }

    private bool CreateLocalDirectoryTree(ServerInfo server, string rootFolder)
    {
        try
        {
            string fullPath = Path.Combine(rootFolder, GetServerDirTree(server));
            if (!Directory.Exists(fullPath))
                Directory.CreateDirectory(fullPath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FTP: не вдалося створити папку для {Unit}/{Sub}", server.Unit, server.Substation);
            return false;
        }
    }

    private static bool StartsWithValidPrefix(string fileName)
    {
        string[] prefixes = ["REXPR", "RECON", "RNET", "RPUSK", "DAILY", "DIAGN"];
        return prefixes.Any(p => fileName.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    private static void ConfigureClient(AsyncFtpClient client)
    {
        client.Config.ConnectTimeout = 10000;
        client.Config.DataConnectionConnectTimeout = 10000;
        client.Config.RetryAttempts = 3;
        client.Encoding = Encoding.UTF8;
        client.Config.ValidateAnyCertificate = true;
        client.Config.DataConnectionType = FtpDataConnectionType.AutoPassive;
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Unknown";
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Trim();
    }
}
