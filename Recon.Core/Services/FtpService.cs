using FluentFTP;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Recon.Core.Constants;
using Recon.Core.Enums;
using Recon.Core.Interfaces;
using Recon.Core.Interfaces.Repositories;
using Recon.Core.Models;
using Recon.Core.Options;
using System.IO;

namespace Recon.Core.Services;

public class FtpService : IFtpService
{
    private readonly IConfigRepository _configRepository;
    private readonly IServerRepository _serverRepository;
    private readonly IAppLogRepository _appLog;
    private readonly IOneDriveService _oneDriveService;
    private readonly IStatisticsService _statsService;
    private readonly IMailService _mailService;
    private readonly IUserRepository _userRepository;

    private string _ftpCacheDir;
    private OneDriveConfig _oneDriveConfig;
    private bool _isOneDriveActive;

    private CancellationTokenSource? _cts;
    private Task? _workingTask;

    public FtpService(IConfigRepository configRepository, IServerRepository serverRepository,
        IAppLogRepository appLog, IOneDriveService oneDriveService, IStatisticsService statsService,
        IMailService mailService, IUserRepository userRepository)
    {
        _configRepository = configRepository;
        _serverRepository = serverRepository;
        _appLog = appLog;
        _oneDriveService = oneDriveService;
        _statsService = statsService;
        _mailService = mailService;
        _userRepository = userRepository;
    }
    
    public void StartFTP()
    {
        if (_workingTask != null && !_workingTask.IsCompleted) return;

        _cts = new CancellationTokenSource();
        _workingTask = Task.Run(() => WorkerLoop(_cts.Token));

        _ = _appLog.LogAsync(LogServiceId.Ftp, "ftp_start");
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
                var rootFolder = await _configRepository.GetRootFolderAsync();
                var config = await _configRepository.GetModuleConfigAsync();
                if (!config.IsFtpActive) break;

                _isOneDriveActive = config.IsOneDriveActive;
                _oneDriveConfig = await _configRepository.GetOneDriveConfigAsync();

                List<ServerInfo> servers = await _serverRepository.GetAllServersAsync();

                _ftpCacheDir = rootFolder + @"/Cache";
                if (!Directory.Exists(_ftpCacheDir))
                    Directory.CreateDirectory(_ftpCacheDir);

                foreach (var server in servers)
                {
                    if (token.IsCancellationRequested) break;

                    CreateLocalDirectoryTree(server, rootFolder);
                    if (_isOneDriveActive) CreateLocalDirectoryTree(server, _oneDriveConfig.Path);

                    await ProcessServerAsync(server, token);
                }
                int feedingTime = await _configRepository.GetFeedingTimeAsync();
                await Task.Delay(TimeSpan.FromSeconds(feedingTime), token);
            }
        }
        catch (OperationCanceledException)
        {
            await _appLog.LogAsync(LogServiceId.Ftp, "ftp_stop");
        }
        catch (Exception ex)
        {
            await _appLog.LogAsync(LogServiceId.Ftp, "loop_error", ex.Message);
            await Task.Delay(5000);
        }
    }
    private string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Unknown";

        char[] invalidChars = Path.GetInvalidFileNameChars();

        foreach (char c in invalidChars)
        {
            name = name.Replace(c, '_');
        }

        return name.Trim();
    }

    private string GetServerDirTree(ServerInfo server)
    {
        string path = string.Empty;
        var unitParts = server.Unit.Split('-', StringSplitOptions.RemoveEmptyEntries);
        
        foreach (var part in unitParts)
        {
            path = Path.Combine(path, SanitizeFileName(part));
        }
        path = Path.Combine(path, SanitizeFileName(server.Substation));
        path = Path.Combine(path, SanitizeFileName(server.Object));
        
        return path;
    }
    private bool CreateLocalDirectoryTree(ServerInfo server, string rootFolder)
    {
        try
        {
            var fullPath = rootFolder;
            fullPath = Path.Combine(fullPath, GetServerDirTree(server));

            if (!Directory.Exists(fullPath))
            {
                Directory.CreateDirectory(fullPath);
            }

            return true;
        }
        catch (Exception ex)
        {
            _ = _appLog.LogAsync(LogServiceId.Ftp, "file_error", $"Не вдалося створити директорію {server.Unit}/{server.Substation}: {ex.Message}");
            return false;
        }
    }
    
    private async Task ProcessServerAsync(ServerInfo server, CancellationToken token)
    {
        try 
        {
            using (var client = new AsyncFtpClient(server.IpAddress, server.Login, server.Password))
            {
                ConfigureClient(client);
            
                await client.Connect();
                server.LastPingTime = DateTime.Now;
                await _serverRepository.UpdateServerStatusAsync(server.StructId, lastPing: server.LastPingTime);

                var items = await client.GetListing(server.RemoteFolderPath);
                if (items.Length == 0) return;

                
                foreach (var item in items)
                {
                    if(token.IsCancellationRequested) break;
                    
                    if (!StartsWithValidPrefix(item.Name)) continue; 

                    await ProcessSingleFileAsync(client, item, server);
                }
            
                await client.Disconnect();
            }
        }
        catch (Exception ex)
        {
            await _appLog.LogAsync(LogServiceId.Ftp, "server_error", $"{server.IpAddress}: {ex.Message}");
        }
    }
    
    // Helper functions
    private bool StartsWithValidPrefix(string fileName)
    {
        string[] validPrefixes = ["REXPR", "RECON", "RNET", "RPUSK", "DAILY", "DIAGN"];
        
        return validPrefixes.Any(prefix => 
            fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }
    
    private void ConfigureClient(AsyncFtpClient client)
    {
        client.Config.ConnectTimeout = 10000;
        client.Config.DataConnectionConnectTimeout = 10000;
        client.Config.RetryAttempts = 3;
        client.Encoding = Encoding.UTF8;
        client.Config.ValidateAnyCertificate = true;
        client.Config.DataConnectionType = FtpDataConnectionType.AutoPassive;
        client.Config.TimeConversion = FtpDate.LocalTime;
    }
    
    private async Task ProcessSingleFileAsync(AsyncFtpClient client, FtpListItem item, ServerInfo server)
    {
        try
        {
            if (!client.IsConnected) await client.Connect();

            string finalLocalPath = Path.Combine(_ftpCacheDir, item.Name);  // RECON353.300
            string tempLocalPath = finalLocalPath + ".tmp";                 // RECON353.300.tmp
            
            var status = await client.DownloadFile(tempLocalPath, item.FullName, FtpLocalExists.Overwrite);
            
            if (status == FtpStatus.Success)
            {
                await _serverRepository.UpdateDailyStatAsync(server.StructId, "collected");
                
                await HandleDownloadedFileAsync(tempLocalPath, item, server);
                await client.DeleteFile(item.FullName);
                
                bool isRecon = item.Name.StartsWith("RECON") || item.Name.StartsWith("REXPR");
                bool isDaily = item.Name.StartsWith("DAILY");
                
                DateTime? updateReconTime = isRecon ? DateTime.Now : null;
                DateTime? updateDailyTime = null;
                
                if (isDaily)
                {
                    updateDailyTime = (item.Modified > DateTime.MinValue) ? item.Modified : DateTime.Now;
                    
                    if (updateDailyTime.Value > server.LastDailyFileDate)
                        server.LastDailyFileDate = updateDailyTime.Value;
                }
                
                await _serverRepository.UpdateServerStatusAsync(
                    server.StructId,
                    lastPing: server.LastPingTime,
                    lastRecon: updateReconTime,
                    lastDaily: updateDailyTime
                );
                
                _statsService.RegisterAction(ServiceType.Ftp, 1);
                
                if (_isOneDriveActive)
                {
                    try
                    {
                        var relativePath = GetServerDirTree(server);
                        relativePath = Path.Combine(relativePath, item.Name);
                        _oneDriveService.CopyToOneDrive(tempLocalPath, relativePath);
                        await _serverRepository.UpdateDailyStatAsync(server.StructId, "uploaded");
                        _statsService.RegisterAction(ServiceType.OneDrive, 1);
                    }
                    catch (IOException ioEx)
                    {
                        await _appLog.LogAsync(LogServiceId.Ftp, "onedrive_error", $"Файл зайнятий: {ioEx.Message}");
                    }
                    catch (Exception ex)
                    {
                        await _appLog.LogAsync(LogServiceId.Ftp, "onedrive_error", ex.Message);
                    }
                }

                try
                {
                    if (File.Exists(finalLocalPath)) File.Delete(finalLocalPath);

                    File.Move(tempLocalPath, finalLocalPath);

                    if (item.Modified > DateTime.MinValue)
                    {
                        File.SetLastWriteTime(finalLocalPath, item.Modified);
                    }
                }
                catch (Exception ex)
                {
                    await _appLog.LogAsync(LogServiceId.Ftp, "file_error", $"Перейменування {item.Name}: {ex.Message}");
                }

                if (isDaily)
                {
                    await SendDailyFileAsync(server, finalLocalPath);
                }

                server.CollectedTimestamps.Add(DateTime.Now);
            }
        }
        catch (Exception ex)
        {
            await _appLog.LogAsync(LogServiceId.Ftp, "file_error", $"{item.Name}: {ex.Message}");
            await client.Disconnect();
        }
    }
    
    private async Task SendDailyFileAsync(ServerInfo server, string filePath)
    {
        try
        {
            bool shouldSend = await _serverRepository.TryMarkDailySentAsync(server.StructId);
            if (!shouldSend) return;

            var recipients = await _userRepository.GetAllUserEmailsAsync();
            if (!recipients.Any()) return;

            string subject = $"Суточний файл: {server.Substation} - {server.Object} | {DateTime.Today:dd.MM.yyyy}";
            string body = $"Прийшов суточний файл.\n\nОб'єкт: {server.Substation} / {server.Object}\n\nЦе повідомлення сформовано автоматично.";

            _ = _mailService.SendToAllAsync(recipients, subject, body, new[] { filePath });
        }
        catch (Exception ex)
        {
            await _appLog.LogAsync(LogServiceId.Ftp, "file_error", $"Відправка суточного файлу {server.Object}: {ex.Message}");
        }
    }

    private async Task HandleDownloadedFileAsync(string localPath, FtpListItem item, ServerInfo server)
    {
        if (item.Name.StartsWith("DAILY") && item.Modified > server.LastDailyFileDate)
            server.LastDailyFileDate = item.Modified;

        await CreateMetaFileAsync(item.Name, server.LocalFolderPath, server.StructId);
    }
    
    private async Task CreateMetaFileAsync(string fileName, string target, int structId)
    {
        var metaData = new { targetPath = target, structId };
        var jsonOptions = new JsonSerializerOptions 
        { 
            WriteIndented = true, 
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping 
        };
    
        string jsonInfo = JsonSerializer.Serialize(metaData, jsonOptions);
        string metaFilePath = Path.Combine(_ftpCacheDir, fileName + ".meta");

        try 
        {
            await File.WriteAllTextAsync(metaFilePath, jsonInfo);
        }
        catch (Exception ex)
        {
            await _appLog.LogAsync(LogServiceId.Ftp, "file_error", $"Мета-файл {fileName}: {ex.Message}");
        }
    }

    private async Task CheckConnection(ServerInfo server)
    {
        
    }
}