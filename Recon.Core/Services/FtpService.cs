using FluentFTP;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Recon.Core.Enums;
using Recon.Core.Interfaces;
using Recon.Core.Models;
using Recon.Core.Options;

namespace Recon.Core.Services;

public class FtpService : IFtpService
{
    private readonly IDatabaseService _dbService;
    private readonly ILogger<FtpService> _logger;
    private readonly IOneDriveService _oneDriveService;
    private readonly IStatisticsService _statsService;
    
    private string _ftpCacheDir;
    private OneDriveConfig _oneDriveConfig;
    private bool _isOneDriveActive;
    
    // Cancel token (our “kill switch”)
    private CancellationTokenSource? _cts;
    private Task? _workingTask;

    public FtpService(IDatabaseService dbService, ILogger<FtpService> logger, IOneDriveService oneDriveService, IStatisticsService statsService)
    {
        _statsService = statsService;
        _dbService = dbService;
        _logger = logger;
        _oneDriveService = oneDriveService;
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
                var rootFolder = _dbService.GetRootFolder();
                
                List<ServerInfo> servers = _dbService.GetAllServers();

                _ftpCacheDir = rootFolder + @"/Cache";
                if (!Directory.Exists(_ftpCacheDir))
                {
                    Directory.CreateDirectory(_ftpCacheDir);
                }

                foreach (var server in servers)
                {
                    if (token.IsCancellationRequested) break;
                    
                    var config = _dbService.GetModuleConfig();
                    if (!config.IsFtpActive) break;
                    
                    _isOneDriveActive = config.IsOneDriveActive;
                    _oneDriveConfig = _dbService.GetOneDriveConfig();
                    
                    CreateLocalDirectoryTree(server, rootFolder);
                    if (_isOneDriveActive) CreateLocalDirectoryTree(server, _oneDriveConfig.Path);

                    await ProcessServerAsync(server, token);
                }
                int feedingTime = _dbService.GetFeedingTime();
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
            _logger.LogError(ex, "FTP Critical Error: Failed to create directory tree for {Unit}/{Substation}",
                server.Unit, server.Substation);
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
                var items = await client.GetListing(server.RemoteFolderPath);
                if (items.Length == 0) return;

                //_logger.LogInformation("Знайдено {Count} файлів на {Ip}", items.Length, server.IpAddress);
                
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
            _logger.LogError(ex, "Критична помилка FTP на сервері {Ip}", server.IpAddress);
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
                await _dbService.UpdateDailyStatAsync(server.Id, "collected");
                
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
                
                await _dbService.UpdateServerStatusAsync(
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
                        await _dbService.UpdateDailyStatAsync(server.Id, "uploaded");
                        _statsService.RegisterAction(ServiceType.OneDrive, 1);
                    }
                    catch (IOException ioEx)
                    {
                        _logger.LogWarning("OneDrive зайнятий, не вдалося оновити файл: {Msg}", ioEx.Message);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError("OneDrive Error: {Msg}", ex.Message);
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
                    _logger.LogError("Не удалось переименовать временный файл: {Msg}", ex.Message);
                }
                
                if (server.CollectedTimestamps == null) server.CollectedTimestamps = new List<DateTime>();
                server.CollectedTimestamps.Add(DateTime.Now);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("Помилка файлу {Name}: {Msg}", item.Name, ex.Message);
            await client.Disconnect(); 
        }
    }
    
    private async Task HandleDownloadedFileAsync(string localPath, FtpListItem item, ServerInfo server)
    {
        if (item.Modified > DateTime.MinValue)
        {
            try 
            {
                File.SetLastWriteTime(localPath, item.Modified);
            } 
            catch (Exception ex) 
            {
                _logger.LogWarning("Дата файлу не змінена: {Msg}", ex.Message);
            }
        }
        
        if (item.Name.StartsWith("DAILY") && item.Modified > server.LastDailyFileDate)
        {
            server.LastDailyFileDate = item.Modified;
        }
        
        await CreateMetaFileAsync(item.Name, server.LocalFolderPath);
    }
    
    private async Task CreateMetaFileAsync(string fileName, string target)
    {
        var metaData = new { targetPath = target };
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
            _logger.LogWarning("Мета-файл не створено: {Msg}", ex.Message);
        }
    }
}