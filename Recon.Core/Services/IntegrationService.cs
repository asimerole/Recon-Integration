using System.Diagnostics;
using System.Text.Json;
using Recon.Core.Constants;
using Recon.Core.Enums;
using Recon.Core.Factories;
using Recon.Core.Interfaces;
using Recon.Core.Interfaces.Repositories;
using Recon.Core.Models;
using System.IO;

namespace Recon.Core.Services;

public class IntegrationService : IIntegrationService
{
    private readonly IFileDataRepository _fileDataRepository;
    private readonly IConfigRepository _configRepository;
    private readonly IServerRepository _serverRepository;
    private readonly IAppLogRepository _appLog;
    private readonly BrokenFileService _brokenFileService;
    private readonly IStatisticsService _statsService;
    private readonly IMailService _mailService;
    
    private static readonly HashSet<string> IgnoredExtensions = new (StringComparer.OrdinalIgnoreCase)
    {
        ".lock",
        ".tmp",
        ".meta"
    };
    
    private int _percentage = 0;
    private bool IsFastBuild { get; set; } = false;
    private bool DbIsFull { get; set; } = false;
    
    private CancellationTokenSource? _cts;
    private Task? _workingTask;

    public IntegrationService(IFileDataRepository fileDataRepository, IConfigRepository configRepository,
        IServerRepository serverRepository, IAppLogRepository appLog,
        BrokenFileService brokenFileService, IStatisticsService statisticsService, IMailService mailService)
    {
        _fileDataRepository = fileDataRepository;
        _configRepository = configRepository;
        _serverRepository = serverRepository;
        _appLog = appLog;
        _brokenFileService = brokenFileService;
        _statsService = statisticsService;
        _mailService = mailService;
    }
    
    public void StartIntegration(IProgress<int> progress = null)
    {
        if (_workingTask is { IsCompleted: false }) return;

        _cts = new CancellationTokenSource();
        _workingTask = Task.Run(() => WorkerLoop(_cts.Token, progress));

        _ = _appLog.LogAsync(LogServiceId.Integration, "integration_start");
    }

    public async Task StopIntegration()
    { 
        if (_cts == null) return;

        try
        {
            await _cts.CancelAsync(); 

            if (_workingTask != null)
            {
                var timeout = Task.Delay(3000);
                var task = await Task.WhenAny(_workingTask, timeout);
            
                if (task == timeout)
                {
                    // Якщо не встиг зупинитися - просто кидаємо його
                    // Але обов'язково обнуляємо посилання нижче!
                }
                else
                {
                    await _workingTask; 
                }
            }
        }
        catch (Exception)
        {
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _workingTask = null; 
        }
    }
    
    public string GetIntegrationPercentage()
    {
        return $"Прогрес інтеграції: {_percentage}%"; 
    }

    public void SetFastBuild(bool isFastBuild)
    {
        IsFastBuild = isFastBuild;
    }
    
    // privates
    
    private async Task WorkerLoop(CancellationToken token, IProgress<int> progress)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var rootFolder = await _configRepository.GetRootFolderAsync();
                var pathToWinRec = await _configRepository.GetWinrecPathAsync();
                var pathToOmp = Path.Combine(pathToWinRec, "OMP_C");
                if (string.IsNullOrEmpty(pathToWinRec) || string.IsNullOrEmpty(rootFolder)) continue;

                var config = await _configRepository.GetModuleConfigAsync();
                
                var globalBatch = new List<FilePair>();
                const int TransactionBatchSize = 500; 
                if (!config.DbIsFull)
                {
                    // --- Full scan ---
                    await ProcessFullArchiveAsync(rootFolder, pathToOmp, globalBatch, TransactionBatchSize, token, progress);
                    if (!token.IsCancellationRequested)
                    {
                        config.DbIsFull = true;
                        await _configRepository.SaveModuleConfigAsync(config);
                    }
                }
                else
                {
                    progress?.Report(100);
                    // --- Only cache folder scan (new files from ftp servers) ---
                    string cachePath = Path.Combine(rootFolder, "Cache");
                    if (Directory.Exists(cachePath))
                    {
                        await ProcessCacheFolderAsync(rootFolder, pathToOmp, token, globalBatch, TransactionBatchSize);
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(5), token);            
            }
            catch (OperationCanceledException)
            {
                await _appLog.LogAsync(LogServiceId.Integration, "integration_stop");
                break;
            }
            catch (Exception ex)
            {
                await _appLog.LogAsync(LogServiceId.Integration, "loop_error", ex.Message);
                await Task.Delay(5000, token);
            }
        }
    }
    
    private async Task ProcessCacheFolderAsync(string rootFolder, 
        string pathToOmpExecutable, 
        CancellationToken token,
        List<FilePair> globalBatch, 
        int transactionBatchSize)
    {
        string cachePath = Path.Combine(rootFolder, "Cache");
        var allFiles = Directory.GetFiles(cachePath)
            .Where(f => !IgnoredExtensions.Contains(Path.GetExtension(f))) 
            .ToList();
        
        foreach (var filePath in allFiles)
        {
            if (token.IsCancellationRequested) return;
            
            string metaPath = filePath + ".meta";
            string targetFolder = null;
            int structId = 0;
            if (File.Exists(metaPath))
            {
                try
                {
                    using (JsonDocument doc = JsonDocument.Parse(await File.ReadAllTextAsync(metaPath)))
                    {
                        if (doc.RootElement.TryGetProperty("targetPath", out JsonElement pathEl))
                            targetFolder = pathEl.GetString();

                        if (doc.RootElement.TryGetProperty("structId", out JsonElement idEl))
                        {
                            if (idEl.ValueKind == JsonValueKind.Number)
                                structId = idEl.GetInt32();
                            else if (idEl.ValueKind == JsonValueKind.String && int.TryParse(idEl.GetString(), out int val))
                                structId = val;
                        }
                    }
                    if (!string.IsNullOrWhiteSpace(targetFolder)) File.Delete(metaPath);
                }
                catch (JsonException exception)
                {
                    await _appLog.LogAsync(LogServiceId.Integration, "file_error", $"Битий JSON: {metaPath}: {exception.Message}");
                    File.Delete(metaPath);
                }
            }
            
            var fileObj = BaseFileFactory.Create(filePath);
            if (fileObj == null || fileObj.ReconNumber == 0)
            {
                continue;
            }
            
            fileObj.StructId = structId;
            
            if (string.IsNullOrEmpty(targetFolder))
            {
                if (fileObj.ReconNumber > 0)
                {
                    targetFolder = await _fileDataRepository.GetTargetFolderByReconIdAsync(fileObj.ReconNumber);
        
                    if (string.IsNullOrEmpty(targetFolder))
                    {
                        continue;
                    }
                }
                else
                {
                    continue; 
                }
            }

            try 
            {
                if (!Directory.Exists(targetFolder)) Directory.CreateDirectory(targetFolder);
                
                await MoveFileToStorageAsync(fileObj, targetFolder);
                File.Delete(metaPath);
                await SortFileIfNeededAsync(fileObj, targetFolder);
            }
            catch (Exception ex)
            {
                await _appLog.LogAsync(LogServiceId.Integration, "file_error", $"Переміщення {fileObj.FileName}: {ex.Message}");
                continue;
            }

            var pair = new FilePair();

            if (fileObj is DataFile df)
            {
                pair.Data = df;
                string expectedRexpr = Path.Combine(cachePath, "REXPR" + df.FileName.Substring(5));
                if (File.Exists(expectedRexpr))
                {
                    pair.Express = CreateExpressObjectAfterGeneration(df);
                    pair.Express.FullPath = expectedRexpr;
                    try
                    {
                        await MoveFileToStorageAsync(pair.Express, pair.Data.ParentFolderPath);
                        File.Delete(expectedRexpr + ".meta");
                    }
                    catch (Exception ex)
                    {
                        await _appLog.LogAsync(LogServiceId.Integration, "file_error", $"Переміщення REXPR {fileObj.FileName}: {ex.Message}");
                    }
                }
            }
            else if (fileObj is ExpressFile ef)
            {
                pair.Express = ef;
                var dataFileName = "RECON" + ef.FileName.Substring(5);
                
                string expectedRecon = Path.Combine(cachePath, dataFileName);
                if (File.Exists(expectedRecon))
                {
                    pair.Data = new DataFile() 
                    { 
                        FullPath = expectedRecon, 
                        FileName = dataFileName,
                        ParentFolderPath = targetFolder 
                    };
                    pair.Data.ParseFileNameProperties(); 
                }
            }
            else if (fileObj is ReconFile rf)
            {
                pair.Other = rf;
                
            }
            
            await ProcessFilePairAsync(pair, rootFolder, pathToOmpExecutable, globalBatch, transactionBatchSize);
            _mailService.AddToQueue(pair);
        }
        if (globalBatch.Count > 0)
        {
            await _fileDataRepository.InsertBatchAsync(globalBatch);
            globalBatch.Clear();
        }
    }
    
    private async Task<T> MoveFileToStorageAsync<T>(T file, string targetFolder) where T : BaseFile
    {
        if (!File.Exists(file.FullPath)) return file;
        try
        {
            string fileName = Path.GetFileName(file.FullPath);
            string destPath = Path.Combine(targetFolder, fileName);

            if (!Directory.Exists(targetFolder))
                Directory.CreateDirectory(targetFolder);

            if (string.Equals(file.FullPath, destPath, StringComparison.OrdinalIgnoreCase))
                return file;

            RemoveReadOnlyAttribute(file.FullPath);

            if (File.Exists(destPath))
            {
                RemoveReadOnlyAttribute(destPath);
                try
                {
                    File.Delete(destPath);
                }
                catch (IOException)
                {
                    await Task.Delay(200);
                    File.Delete(destPath);
                }
            }

            await MoveFileWithRetryAsync(file.FullPath, destPath);

            file.FullPath = destPath;
            file.ParentFolderPath = targetFolder;

            return file;
        }
        catch (Exception ex)
        {
            await _appLog.LogAsync(LogServiceId.Integration, "file_error", $"Не вдалося перемістити {file.FileName}: {ex.Message}");
            return file;
        }
    }
    
    private ExpressFile CreateExpressObjectAfterGeneration(DataFile dataFile)
    {
        string baseName = dataFile.FileName.Substring(5); // 353.704
        string newFileName = "REXPR" + baseName;
        string newFullPath = Path.Combine(dataFile.ParentFolderPath, newFileName);

        var newExpress = new ExpressFile
        {
            FileName = newFileName,
            FullPath = newFullPath,
            ParentFolderPath = dataFile.ParentFolderPath,
            ReconNumber = dataFile.ReconNumber,
            FileNum = dataFile.FileNum,
            FilePrefix = "REXPR"
        };
        return newExpress;
    }
    
private async Task ProcessFullArchiveAsync(string rootFolder, string pathToWinRec, List<FilePair> batch, int transactionBatchSize, CancellationToken token, IProgress<int> progress)
{
    var allDirectories = Directory.EnumerateDirectories(rootFolder, "*", SearchOption.AllDirectories)
        .Where(d => !d.Contains("\\Cache", StringComparison.OrdinalIgnoreCase))
        .ToList();
    
    if (token.IsCancellationRequested) return;
    
    HashSet<string> validObjectPaths = await FillStructureAsync(allDirectories, rootFolder, token);
    
    int totalFolders = validObjectPaths.Count;

    if (totalFolders == 0) return; 

    int processedFolders = 0;
    int skippedByDate = 0;
    
    progress?.Report(0);
    
    if (token.IsCancellationRequested) return;
    
    DateTime cutOffDate = DateTime.Now.AddDays(-60);
    
    foreach (var objectPath in validObjectPaths)
    {
        if (token.IsCancellationRequested) return;
        
        try
        {
            if (IsFastBuild)
            {
                var dirInfo = new DirectoryInfo(objectPath);
                if (dirInfo.LastWriteTime < cutOffDate)
                {
                    skippedByDate++;
                    processedFolders++;
                    progress?.Report((int)((double)processedFolders / totalFolders * 100));
                    continue;
                }
            }
            
            await IntegrateObjectFilesAsync(objectPath, rootFolder, pathToWinRec, 
                token, batch, transactionBatchSize);
            
            processedFolders++;

            int percent = (int)((double)processedFolders / totalFolders * 100);
            progress?.Report(percent);
        }
        catch (UnauthorizedAccessException)
        {
            await _appLog.LogAsync(LogServiceId.Integration, "access_error", $"Немає доступу: {objectPath}");
            processedFolders++;
            progress?.Report((int)((double)processedFolders / totalFolders * 100));
        }
        catch (Exception ex)
        {
            await _appLog.LogAsync(LogServiceId.Integration, "object_error", $"{objectPath}: {ex.Message}");
            batch.Clear();
            processedFolders++;
            progress?.Report((int)((double)processedFolders / totalFolders * 100));
        }
    }
    if (batch.Count > 0)
    {
        await _fileDataRepository.InsertBatchAsync(batch);
        batch.Clear();
    }
}
    
private async Task IntegrateObjectFilesAsync(string objectPath, string rootFolder, string pathToOmpExecutable, CancellationToken token, List<FilePair> globalBatch, int transactionBatchSize)
{
    var groupedFiles = new Dictionary<string, FilePair>();
    
    int rawFilesCount = 0;
    int ignoredCount = 0;
    int skippedInvalidCount = 0;

    var allObjectFiles = Directory.EnumerateFiles(objectPath, "*", SearchOption.AllDirectories);

    foreach (var filePath in allObjectFiles)
    {
        if (token.IsCancellationRequested) return;
        
        rawFilesCount++; 

        if (IgnoredExtensions.Contains(Path.GetExtension(filePath))) 
        {
            ignoredCount++;
            continue;
        }
        
        var file = BaseFileFactory.Create(filePath); 
        if (file == null || file.ReconNumber == 0) 
        {
            skippedInvalidCount++;
            continue;
        }
        
        await SortFileIfNeededAsync(file, objectPath);

        string key = $"{file.ReconNumber}.{file.FileNum}.{file.Timestamp:yyyyMM}";
        
        if (!groupedFiles.TryGetValue(key, out var pair))
        {
            pair = new FilePair();
            groupedFiles.Add(key, pair);
        }
        
        if (file is DataFile df) pair.Data = df;
        else if (file is ExpressFile ef) pair.Express = ef;
        else if (file is ReconFile rf) pair.Other = rf;
    }

    foreach (var pair in groupedFiles.Values)
    {
        if (token.IsCancellationRequested) return;
        
        await ProcessFilePairAsync(pair, rootFolder, pathToOmpExecutable, globalBatch, transactionBatchSize);
    }
    
    await _brokenFileService.SaveLogAsync();
}

    private static async Task<bool> TryGenerateExpressFileAsync(string programPath, string inputFilePath)
    {
        string programName = Path.GetFileName(programPath);
        string arguments = $"\"{inputFilePath}\" -N";
        const int TimeoutMilliseconds = 2000;
        
        using (var process = new Process())
        {
            try
            {
                process.StartInfo.FileName = programPath;
                process.StartInfo.Arguments = arguments;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.CreateNoWindow = true; 
                
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;

                if (!process.Start())
                    return false;

                var processCompletionTask = process.WaitForExitAsync();
                var finishedTask = await Task.WhenAny(processCompletionTask, Task.Delay(TimeoutMilliseconds));

                if (finishedTask == processCompletionTask)
                {
                    process.WaitForExit();
                    return process.ExitCode == 0;
                }
                else
                {
                    try { process.Kill(true); }
                    catch (InvalidOperationException) { }
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{programName}]: Exception during external program execution. {ex.Message}");
                return false;
            }
        }
    }

    private async Task<HashSet<string>> FillStructureAsync(List<string> allDirectories, string rootPath, CancellationToken token)
    {
        var processedObjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reconNumRegex = new System.Text.RegularExpressions.Regex(@"^.{5}(\d{3})", System.Text.RegularExpressions.RegexOptions.Compiled);
        var dateFolderRegex = new System.Text.RegularExpressions.Regex(@"^\d{4}_\d{2}$", System.Text.RegularExpressions.RegexOptions.Compiled);
        char sep = Path.DirectorySeparatorChar;
        
        foreach (var folder in allDirectories)
        {
            if (token.IsCancellationRequested) return null!;
        
            string relativePath = Path.GetRelativePath(rootPath, folder);
            
            if (relativePath == "." || string.IsNullOrEmpty(relativePath)) continue;
            
            var parts = relativePath.Split(sep);
            if (parts.Length < 3) continue;
            
            try
            {
                string folderName = Path.GetFileName(folder);
                string objectFolderPath = folder;
        
                // If folder name (2000_07), folder level up
                bool isDateFolder = dateFolderRegex.IsMatch(folderName);
                if (isDateFolder)
                {
                    var parent = Directory.GetParent(folder);
                    if (parent != null)
                    {
                        objectFolderPath = parent.FullName;
                        
                        // We need to recalculate the parts for the parent to write correctly to the database.
                        // Remove the last part (date) from the parts array.
                        // Was: [OSR, DTEKV, Vuzlova, 2000_07]
                        // Will become: [OSR, DTEKV, Vuzlova]
                        Array.Resize(ref parts, parts.Length - 1);
                    }
                }
        
                // if object exests - skip
                if (processedObjects.Contains(objectFolderPath)) continue;
        
                // 2.  Parsing the path relative to the root
                // root: C:\Data
                // path: C:\Data\ОСР\ДТЕКВМ\ЦД\Вузлова\1СШ150
                // relative: ОСР\ДТЕКВМ\ЦД\Вузлова\1СШ150
        
                var files = Directory.EnumerateFiles(folder).Take(50);
                int reconNumber = -1;
                
                foreach (var file in files)
                {
                    var match = reconNumRegex.Match(Path.GetFileName(file));
                    if (match.Success && int.TryParse(match.Groups[1].Value, out int rNum))
                    {
                        reconNumber = rNum;
                        break;
                    }
                }
        
                if (reconNumber == -1) continue;
                if (parts.Length < 3) continue; 
        
                string objectName = parts[parts.Length - 1];        // Vuzlova
                string substationName = parts[parts.Length - 2];    // DTEKVM
                
                string unitName = string.Join(" - ", parts.Take(parts.Length - 2));
        
                // Insert into DB
                await _fileDataRepository.EnsureStructureExistsAsync(
                    unitName, 
                    substationName, 
                    objectName, 
                    reconNumber, 
                    objectFolderPath); 
        
                processedObjects.Add(objectFolderPath);
            }
            catch (Exception ex)
            {
                await _appLog.LogAsync(LogServiceId.Integration, "file_error", $"Розбір структури {folder}: {ex.Message}");
            }
        }

        return processedObjects;
    }
    
    private async Task SortFileIfNeededAsync(BaseFile file, string objectRootPath)
    {
        string currentDir = Path.GetDirectoryName(file.FullPath) ?? "";
        string dirName = Path.GetFileName(currentDir);
        if (System.Text.RegularExpressions.Regex.IsMatch(dirName, @"^\d{4}_\d{2}$")) return;

        try
        {
            DateTime fileDate = File.GetLastWriteTime(file.FullPath);
            string targetFolderName = $"{fileDate.Year:D4}_{fileDate.Month:D2}";
            string targetDir = Path.Combine(objectRootPath, targetFolderName);
            string targetPath = Path.Combine(targetDir, file.FileName);

            if (string.Equals(file.FullPath, targetPath, StringComparison.OrdinalIgnoreCase)) return;

            if (!Directory.Exists(targetDir))
                Directory.CreateDirectory(targetDir);

            RemoveReadOnlyAttribute(file.FullPath);

            if (File.Exists(targetPath))
            {
                RemoveReadOnlyAttribute(file.FullPath);
                File.Delete(file.FullPath);
            }
            else
            {
                await MoveFileWithRetryAsync(file.FullPath, targetPath);
            }

            file.FullPath = targetPath;
            file.ParentFolderPath = targetDir;
        }
        catch (Exception ex)
        {
            await _appLog.LogAsync(LogServiceId.Integration, "file_error", $"Сортування {file.FileName}: {ex.Message}");
        }
    }
    
    private void RemoveReadOnlyAttribute(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                {
                    File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                }
            }
        }
        catch { /* Ignore if the attribute could not be removed */ }
    }

    private async Task MoveFileWithRetryAsync(string source, string dest, int maxRetries = 3)
    {
        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                File.Move(source, dest);
                return;
            }
            catch (IOException)
            {
                if (i == maxRetries - 1) throw;
                await Task.Delay(500);
            }
            catch (UnauthorizedAccessException)
            {
                RemoveReadOnlyAttribute(source);
                if (i == maxRetries - 1) throw;
                await Task.Delay(500);
            }
        }
    }

    private async Task ProcessFilePairAsync(
        FilePair pair, 
        string rootFolder, 
        string pathToOmpExecutable, 
        List<FilePair> globalBatch, 
        int transactionBatchSize)
    {
        var hasData = pair.Data != null;
        var hasExpress = pair.Express != null;
        var hasOther = pair.Other != null;

        if (hasData && !hasExpress)
        {
            var success = await TryGenerateExpressFileAsync(pathToOmpExecutable, pair.Data.FullPath);
            if (success)
            {
                pair.Express = CreateExpressObjectAfterGeneration(pair.Data);
                hasExpress = true;
            }
            else
            {
                _brokenFileService.LogBrokenFile(pair.Data.FullPath, "Recon without Rexpr (OMP_C failed)");
            }
        }
            
        if (hasExpress)
        {
            // REXPR is always processed first to obtain an accurate timestamp
            await pair.Express!.ProcessAsync(rootFolder);
            
        }
        
        if (hasData)
        {
            await pair.Data!.ProcessAsync(rootFolder);
            
            // If REXPR exists, the event date takes precedence over the modification date.
            if (hasExpress)
            {
                pair.Data.Timestamp = pair.Express!.Timestamp;
            }
        }

        if (hasOther)
        {
            await pair.Other!.ProcessAsync(rootFolder);
        }

        await _serverRepository.UpdateDailyStatAsync(pair.StructId, "integrated");
        
        globalBatch.Add(pair);
        if (globalBatch.Count >= transactionBatchSize)
        {
            await _fileDataRepository.InsertBatchAsync(globalBatch);
            _statsService.RegisterAction(ServiceType.Integration, globalBatch.Count);
            globalBatch.Clear();
        }
    }
}