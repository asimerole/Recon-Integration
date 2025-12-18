using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Recon.Core.Enums;
using Recon.Core.Factories;
using Recon.Core.Interfaces;
using Recon.Core.Models;

namespace Recon.Core.Services;

public class IntegrationService : IIntegrationService
{
    private readonly IDatabaseService _databaseService;
    private readonly ILogger<IIntegrationService> _logger;
    private readonly BrokenFileService _brokenFileService;
    private readonly IStatisticsService _statsService;
    
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

    public IntegrationService(IDatabaseService databaseService, ILogger<IIntegrationService> logger, IStatisticsService statisticsService)
    {
        _brokenFileService = new BrokenFileService(logger);
        _statsService = statisticsService;
        _databaseService = databaseService;
        _logger = logger;
    }
    
    public void StartIntegration(IProgress<int> progress = null)
    {
        if (_workingTask is { IsCompleted: false }) return; 
        
        _cts = new CancellationTokenSource();
        
        _workingTask = Task.Run(() => WorkerLoop(_cts.Token, progress));
    }

    public void StopIntegration()
    {
        if (_cts == null) return;
        
        _cts.Cancel(); 
        _cts = null;
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
                var rootFolder = _databaseService.GetRootFolder();
                var pathToWinRec = _databaseService.GetWinrecPath();
                var pathToOmp = string.Concat(pathToWinRec, "/OMP_C");
                if(string.IsNullOrEmpty(pathToWinRec) || string.IsNullOrEmpty(rootFolder)) continue;
                
                var config = _databaseService.GetModuleConfig();
                
                var globalBatch = new List<FilePair>();
                const int TransactionBatchSize = 1000; 
                if (!config.DbIsFull)
                {
                    // --- Full scan ---
                    await ProcessFullArchiveAsync(rootFolder, pathToOmp, globalBatch, TransactionBatchSize, token, progress);
                    if (!token.IsCancellationRequested)
                    {
                        config.DbIsFull = true;
                        _databaseService.SaveModuleConfig(config);
                    }
                }
                else
                {
                    progress.Report(100);
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
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Критична помилка в циклі інтеграції");
                await Task.Delay(5000);
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
            if (File.Exists(metaPath))
            {
                try 
                {
                    using (JsonDocument doc = JsonDocument.Parse(await File.ReadAllTextAsync(metaPath)))
                    { 
                        if (doc.RootElement.TryGetProperty("targetPath", out JsonElement pathElement))
                        {
                            targetFolder = pathElement.GetString();
                        }
                    }
                    if (!string.IsNullOrWhiteSpace(targetFolder)) File.Delete(metaPath);
                }
                catch (JsonException) 
                {
                    File.Delete(metaPath);
                }  
            } 
            
            var fileObj = BaseFileFactory.Create(filePath);
            if (fileObj == null) continue;
            
            if (string.IsNullOrEmpty(targetFolder))
            {
                if (fileObj.ReconNumber > 0)
                {
                    targetFolder = await _databaseService.GetTargetFolderByReconIdAsync(fileObj.ReconNumber);
        
                    if (!string.IsNullOrEmpty(targetFolder))
                    {
                        // _logger.LogInformation($"Восстановлен путь для файла {fileObj.FileName} через БД: {targetFolder}");
                    }
                    else
                    {
                        //_logger.LogWarning($"Не удалось найти путь для ReconID={fileObj.ReconNumber}. Файл пропускается: {filePath}");
                        continue; // Мы сделали всё что могли. Файл остается в Cache до лучших времен.
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
                
                MoveFileToStorage(fileObj, targetFolder);
                File.Delete(metaPath);
                SortFileIfNeeded(fileObj, targetFolder);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Помилка переміщення файлу {fileObj.FileName}: {ex.Message}");
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
                        MoveFileToStorage(pair.Express, pair.Data.ParentFolderPath);
                        File.Delete(expectedRexpr + ".meta");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"Помилка переміщення файлу {fileObj.FileName}: {ex.Message}");
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
        }
        if (globalBatch.Count > 0)
        {
            await _databaseService.InsertBatchAsync(globalBatch);
            globalBatch.Clear();
        }
    }
    
    private T MoveFileToStorage<T>(T file, string targetFolder) where T : BaseFile
    {
        try
        {
            string fileName = Path.GetFileName(file.FullPath);
            string destPath = Path.Combine(targetFolder, fileName);
            
            File.Move(file.FullPath, destPath, overwrite: true);
            
            file.FullPath = destPath;
            file.ParentFolderPath = targetFolder;
        
            return file;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Не вдалося перемістити файл {file.FileName}: {ex.Message}");
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
            _logger.LogWarning("Немає доступу до папки: {Dir}", objectPath);
            processedFolders++;
            progress?.Report((int)((double)processedFolders / totalFolders * 100));
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Помилка при обробці об'єкта {Path}: {Msg}", objectPath, ex.Message);
            batch.Clear();
            
            processedFolders++;
            progress?.Report((int)((double)processedFolders / totalFolders * 100));
        }
    }
    if (batch.Count > 0)
    {
        await _databaseService.InsertBatchAsync(batch);
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
        
        SortFileIfNeeded(file, objectPath);

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
                {
                    //_logger.LogError($"[{programName}]: Failed to start process.");
                    return false;
                }
                
                var processCompletionTask = process.WaitForExitAsync(); 
                
                var finishedTask = await Task.WhenAny(processCompletionTask, Task.Delay(TimeoutMilliseconds));

                if (finishedTask == processCompletionTask)
                {
                    //await processCompletionTask; 
                    
                    process.WaitForExit();
                    
                    return process.ExitCode == 0;
                }
                else
                {
                    //_logger.LogWarning($"[{programName}]: The process timed out and will be terminated for file: {inputFilePath}");
                    try
                    {
                        process.Kill(true); 
                    }
                    catch (InvalidOperationException)
                    {
                        // Процес міг вже завершитися між перевіркою та Kill
                    }
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{programName}]: Exception during external program execution. {ex.Message}");
                //_logger.LogError(ex, $"[{programName}]: Exception during external program execution.");
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
                await _databaseService.EnsureStructureExistsAsync(
                    unitName, 
                    substationName, 
                    objectName, 
                    reconNumber, 
                    objectFolderPath); 
        
                processedObjects.Add(objectFolderPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Помилка при розборі структури папки {Folder}: {Msg}", folder, ex.Message);
            }
        }

        return processedObjects;
    }
    
    private void SortFileIfNeeded(BaseFile file, string objectRootPath)
    {
        // If the file is already where it should be, exit (your RegEx is correct)
        string currentDir = Path.GetDirectoryName(file.FullPath) ?? "";
        string dirName = Path.GetFileName(currentDir);
        if (System.Text.RegularExpressions.Regex.IsMatch(dirName, @"^\d{4}_\d{2}$")) return;

        try
        {
            // 1. Specify the destination folder
            DateTime fileDate = File.GetLastWriteTime(file.FullPath);
            string targetFolderName = $"{fileDate.Year:D4}_{fileDate.Month:D2}";
            string targetDir = Path.Combine(objectRootPath, targetFolderName);
            string targetPath = Path.Combine(targetDir, file.FileName);

            // If the path has not changed (the file is already there)
            if (string.Equals(file.FullPath, targetPath, StringComparison.OrdinalIgnoreCase)) return;

            if (!Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            // 2. Remove the ReadOnly attribute (this is a common cause of Access Denied)
            RemoveReadOnlyAttribute(file.FullPath);

            // 3. Movement logic
            if (File.Exists(targetPath))
            {
                // 4. Update the path in the object so that the program continues to work with the new location

                _logger.LogInformation($"Файл {file.FileName} вже існує в цільовій папці. Видаляємо дублікат.");
            
                RemoveReadOnlyAttribute(file.FullPath); 
                File.Delete(file.FullPath);
            }
            else
            {
                // Scenario: Move from Retry (if file is busy)
                MoveFileWithRetry(file.FullPath, targetPath);
            }

            // 4. Update the path in the object so that the program continues to work with the new location
            file.FullPath = targetPath; 
            file.ParentFolderPath = targetDir;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Не вдалося відсортувати {File}: {Msg}", file.FileName, ex.Message);
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

    private void MoveFileWithRetry(string source, string dest, int maxRetries = 3)
    {
        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                File.Move(source, dest);
                return; 
            }
            catch (IOException) // File is busy
            {
                if (i == maxRetries - 1) throw; // Last attempt - throw an error
                System.Threading.Thread.Sleep(500); // Waiting 0.5 seconds
            }
            catch (UnauthorizedAccessException) // No permissions or ReadOnly
            {
                // Let's try removing the attributes again, in case something has changed.
                RemoveReadOnlyAttribute(source);
                if (i == maxRetries - 1) throw;
                System.Threading.Thread.Sleep(500);
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
        
        globalBatch.Add(pair);
        if (globalBatch.Count >= transactionBatchSize)
        {
            await _databaseService.InsertBatchAsync(globalBatch);
            // Після успішного InsertBatchAsync
            _statsService.RegisterAction(ServiceType.Integration, globalBatch.Count);
            globalBatch.Clear();
            //_logger.LogInformation("Записано пачку {Count} файлів...", transactionBatchSize);
        }
    }
}