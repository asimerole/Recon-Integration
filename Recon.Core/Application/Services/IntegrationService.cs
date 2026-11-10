using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Recon.Core.Application.Interfaces;
using Recon.Core.Application.Interfaces.Repositories;
using Recon.Core.Domain.Constants;
using Recon.Core.Domain.Enums;
using Recon.Core.Domain.Factories;
using Recon.Core.Domain.Models;

namespace Recon.Core.Application.Services;

/// <summary>
/// Puts registrator files into the storage (root_directory) and the database.
///   Full scan (db not filled yet): every object folder → pairs → DB. Objects are read in parallel,
///   one writer sends batches to the DB.
///   Cache mode: new files from FTP in root\Cache → object folder → DB, OneDrive queue, mail outbox.
/// </summary>
public class IntegrationService : IIntegrationService
{
    private const int MaxBatchPairs = 500;
    private const long MaxBatchBytes = 64L * 1024 * 1024;   // files are held in memory until written
    private const int ParallelObjects = 4;
    private const int ChannelCapacity = 100;
    private static readonly TimeSpan OmpTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CachePassDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FastBuildWindow = TimeSpan.FromDays(60);
    // A file that still can't be placed after this long won't resolve by itself
    private static readonly TimeSpan UnresolvedAfter = TimeSpan.FromDays(1);

    private static readonly Regex MonthFolderRegex = new(@"^\d{4}_\d{2}$", RegexOptions.Compiled);
    private static readonly Regex ReconNumRegex = new(@"^.{5}(\d{3})", RegexOptions.Compiled);

    private static readonly HashSet<string> IgnoredExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".lock",
        ".tmp",
        ".meta"
    };

    private readonly IFileDataRepository _fileDataRepository;
    private readonly IConfigRepository _configRepository;
    private readonly IServerRepository _serverRepository;
    private readonly IAppLogRepository _appLog;
    private readonly IStatisticsService _statsService;
    private readonly IMailService _mailService;
    private readonly IOneDriveService _oneDriveService;

    private volatile bool _isFastBuild;
    private readonly object _stateLock = new();
    private CancellationTokenSource? _cts;
    private Task? _workingTask;

    public IntegrationService(IFileDataRepository fileDataRepository, IConfigRepository configRepository,
        IServerRepository serverRepository, IAppLogRepository appLog, IStatisticsService statisticsService,
        IMailService mailService, IOneDriveService oneDriveService)
    {
        _fileDataRepository = fileDataRepository;
        _configRepository = configRepository;
        _serverRepository = serverRepository;
        _appLog = appLog;
        _statsService = statisticsService;
        _mailService = mailService;
        _oneDriveService = oneDriveService;
    }

    public void StartIntegration(IProgress<int>? progress = null)
    {
        lock (_stateLock)
        {
            if (_workingTask is { IsCompleted: false }) return;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _workingTask = Task.Run(() => WorkerLoopAsync(token, progress));
        }
        _ = _appLog.LogAsync(LogServiceId.Integration, "integration_start");
    }

    public async Task StopIntegration()
    {
        CancellationTokenSource? cts;
        Task? task;
        lock (_stateLock)
        {
            cts = _cts;
            task = _workingTask;
            _cts = null;
            _workingTask = null;
        }
        if (cts == null) return;

        await cts.CancelAsync();
        if (task != null)
        {
            // A batch already sent to SQL finishes on its own; don't hang the UI on it
            await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)));
        }
        cts.Dispose();
    }

    public void SetFastBuild(bool isFastBuild) => _isFastBuild = isFastBuild;

    // ───────────── loop ─────────────

    private async Task WorkerLoopAsync(CancellationToken token, IProgress<int>? progress)
    {
        bool settingsMissingLogged = false;
        while (!token.IsCancellationRequested)
        {
            try
            {
                string rootFolder = await _configRepository.GetRootFolderAsync();
                string winrec = await _configRepository.GetWinrecPathAsync();
                if (string.IsNullOrWhiteSpace(rootFolder) || string.IsNullOrWhiteSpace(winrec))
                {
                    // Used to `continue` without any pause — a busy loop hammering the DB
                    if (!settingsMissingLogged)
                        await _appLog.LogAsync(LogServiceId.Integration, "loop_error",
                            "Не задано root_directory або winrec-bs, інтеграцію призупинено до налаштування");
                    settingsMissingLogged = true;
                    await Task.Delay(TimeSpan.FromMinutes(1), token);
                    continue;
                }
                settingsMissingLogged = false;

                string ompPath = Path.Combine(winrec, "OMP_C");
                var config = await _configRepository.GetModuleConfigAsync();

                if (!config.DbIsFull)
                {
                    if (await ProcessFullArchiveAsync(rootFolder, ompPath, progress, token))
                    {
                        // Re-read: a full scan takes hours, other flags may have changed meanwhile
                        var fresh = await _configRepository.GetModuleConfigAsync();
                        fresh.DbIsFull = true;
                        await _configRepository.SaveModuleConfigAsync(fresh);
                    }
                }
                else
                {
                    progress?.Report(100);
                    string cachePath = Path.Combine(rootFolder, "Cache");
                    if (Directory.Exists(cachePath))
                        await ProcessCacheFolderAsync(rootFolder, ompPath, cachePath, token);
                }

                await Task.Delay(CachePassDelay, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                await _appLog.LogAsync(LogServiceId.Integration, "loop_error", ex.Message);
                try { await Task.Delay(TimeSpan.FromSeconds(30), token); } catch (OperationCanceledException) { break; }
            }
        }
        await _appLog.LogAsync(LogServiceId.Integration, "integration_stop");
    }

    // ───────────── cache mode ─────────────

    private async Task ProcessCacheFolderAsync(string rootFolder, string ompPath, string cachePath, CancellationToken token)
    {
        var allFiles = Directory.GetFiles(cachePath)
            .Where(f => !IgnoredExtensions.Contains(Path.GetExtension(f)))
            .ToList();
        var pairs = new List<FilePair>();
        var broken = new List<string>();

        foreach (var filePath in allFiles)
        {
            token.ThrowIfCancellationRequested();
            // REXPR/RECON may already have been moved earlier in this pass together with its pair
            if (!File.Exists(filePath)) continue;

            string metaPath = filePath + ".meta";
            var (targetFolder, structId) = await ReadMetaAsync(metaPath);

            var fileObj = BaseFileFactory.Create(filePath);
            if (fileObj == null || fileObj.ReconNumber == 0)
            {
                await ParkIfStaleAsync(cachePath, filePath, "не вдалося розібрати ім'я файлу (номер реєстратора)");
                continue;
            }
            fileObj.StructId = structId;

            if (string.IsNullOrEmpty(targetFolder))
            {
                targetFolder = await _fileDataRepository.GetTargetFolderByReconIdAsync(fileObj.ReconNumber);
                if (string.IsNullOrEmpty(targetFolder))
                {
                    await ParkIfStaleAsync(cachePath, filePath, $"реєстратор №{fileObj.ReconNumber} не знайдено в struct");
                    continue;
                }
            }

            // Not moved = still in Cache: skip, the next pass retries (processing it from Cache gave orphans)
            if (!await MoveFileToStorageAsync(fileObj, targetFolder)) continue;
            TryDelete(metaPath);
            await SortFileAsync(fileObj, targetFolder, File.GetLastWriteTime(fileObj.FullPath));

            var pair = new FilePair();
            if (fileObj is DataFile df)
            {
                pair.Data = df;
                string expectedRexpr = Path.Combine(cachePath, "REXPR" + df.FileName[BaseFile.PrefixLength..]);
                if (File.Exists(expectedRexpr))
                {
                    var express = (ExpressFile)BaseFileFactory.Create(expectedRexpr)!;
                    express.StructId = structId;
                    if (await MoveFileToStorageAsync(express, Path.GetDirectoryName(df.FullPath)!))
                    {
                        pair.Express = express;
                        TryDelete(expectedRexpr + ".meta");
                    }
                }
            }
            else if (fileObj is ExpressFile ef)
            {
                pair.Express = ef;
                string expectedRecon = Path.Combine(cachePath, "RECON" + ef.FileName[BaseFile.PrefixLength..]);
                if (File.Exists(expectedRecon))
                {
                    var data = (DataFile)BaseFileFactory.Create(expectedRecon)!;
                    data.StructId = structId;
                    // Otherwise RECON stays in Cache and is processed again on the next pass
                    if (await MoveFileToStorageAsync(data, Path.GetDirectoryName(ef.FullPath)!))
                    {
                        pair.Data = data;
                        TryDelete(expectedRecon + ".meta");
                    }
                }
            }
            else if (fileObj is ReconFile rf)
            {
                pair.Other = rf;
            }

            await CompletePairAsync(pair, rootFolder, ompPath, broken);
            pairs.Add(pair);

            await _serverRepository.UpdateDailyStatAsync(pair.StructId, "integrated");
            EnqueuePairToOneDrive(pair, rootFolder, cachePath);
            await _mailService.AddToQueueAsync(pair);
        }

        var stats = new WriteStats();
        foreach (var chunk in pairs.Chunk(MaxBatchPairs))
            await WriteBatchAsync(chunk, stats);

        if (broken.Count > 0)
            await LogBrokenAsync(cachePath, broken);

        DeleteCacheLeftovers(cachePath);
    }

    private async Task<(string? TargetFolder, int StructId)> ReadMetaAsync(string metaPath)
    {
        if (!File.Exists(metaPath)) return (null, 0);
        try
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(metaPath));
            string? target = doc.RootElement.TryGetProperty("targetPath", out var p) ? p.GetString() : null;
            int structId = 0;
            if (doc.RootElement.TryGetProperty("structId", out var id))
            {
                if (id.ValueKind == JsonValueKind.Number) structId = id.GetInt32();
                else if (id.ValueKind == JsonValueKind.String && int.TryParse(id.GetString(), out int v)) structId = v;
            }
            // .meta is deleted only after a successful move: if the file is skipped now,
            // the next pass still needs its target path
            return (target, structId);
        }
        catch (JsonException ex)
        {
            await _appLog.LogAsync(LogServiceId.Integration, "file_error", $"Битий JSON: {metaPath}: {ex.Message}");
            TryDelete(metaPath);
            return (null, 0);
        }
    }

    private async Task ParkIfStaleAsync(string cachePath, string filePath, string reason)
    {
        try
        {
            // CreationTime = when it landed in Cache (LastWriteTime is the device's event time)
            if (DateTime.Now - File.GetCreationTime(filePath) < UnresolvedAfter) return;

            string parkDir = Path.Combine(cachePath, "Unresolved");
            Directory.CreateDirectory(parkDir);
            string dest = Path.Combine(parkDir, Path.GetFileName(filePath));

            File.Move(filePath, dest, overwrite: true);
            if (File.Exists(filePath + ".meta"))
                File.Move(filePath + ".meta", dest + ".meta", overwrite: true);

            await _appLog.LogAsync(LogServiceId.Integration, "file_unresolved",
                $"{Path.GetFileName(filePath)} → Cache\\Unresolved: {reason}");
        }
        catch (Exception ex)
        {
            await _appLog.LogAsync(LogServiceId.Integration, "file_error",
                $"Перенесення {Path.GetFileName(filePath)} в Unresolved: {ex.Message}");
        }
    }

    // .meta without its file and .tmp from interrupted downloads are never picked up by anything
    private static void DeleteCacheLeftovers(string cachePath)
    {
        foreach (var path in Directory.GetFiles(cachePath))
        {
            try
            {
                string ext = Path.GetExtension(path);
                bool orphanMeta = ext.Equals(".meta", StringComparison.OrdinalIgnoreCase)
                                  && !File.Exists(path[..^".meta".Length]);
                bool staleTmp = ext.Equals(".tmp", StringComparison.OrdinalIgnoreCase);

                if (!orphanMeta && !staleTmp) continue;

                // FTP may stamp a fresh download with the device's old modified time — take the latest of both
                DateTime touched = File.GetCreationTime(path) > File.GetLastWriteTime(path)
                    ? File.GetCreationTime(path) : File.GetLastWriteTime(path);
                if (DateTime.Now - touched > UnresolvedAfter)
                    File.Delete(path);
            }
            catch { /* locked by FTP right now — next pass */ }
        }
    }

    // Non-blocking: OneDriveService copies in its own queue (and ignores this while the module is off)
    private void EnqueuePairToOneDrive(FilePair pair, string rootFolder, string cachePath)
    {
        foreach (BaseFile? file in new BaseFile?[] { pair.Data, pair.Express, pair.Other })
        {
            if (file == null || !File.Exists(file.FullPath)) continue;
            // A paired file left in Cache is not in its final place yet; it is copied on its own pass
            if (file.FullPath.StartsWith(cachePath, StringComparison.OrdinalIgnoreCase)) continue;

            _oneDriveService.Enqueue(file.FullPath, rootFolder, pair.StructId);
        }
    }

    // ───────────── full scan ─────────────

    /// <returns>true if the whole archive was processed (not cancelled)</returns>
    private async Task<bool> ProcessFullArchiveAsync(string rootFolder, string ompPath, IProgress<int>? progress,
        CancellationToken token)
    {
        var started = Stopwatch.StartNew();
        string cacheRoot = Path.Combine(rootFolder, "Cache");
        var directories = Directory.EnumerateDirectories(rootFolder, "*", SearchOption.AllDirectories)
            .Where(d => !d.StartsWith(cacheRoot, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var objects = await FillStructureAsync(directories, rootFolder, token);
        if (objects.Count == 0) return !token.IsCancellationRequested;

        progress?.Report(0);
        await _appLog.LogAsync(LogServiceId.Integration, "integration_full_start", $"Об'єктів: {objects.Count}");

        var channel = Channel.CreateBounded<FilePair>(new BoundedChannelOptions(ChannelCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        var stats = new WriteStats();
        var writer = Task.Run(() => WriteFromChannelAsync(channel.Reader, stats, token));

        int done = 0, skipped = 0;
        DateTime fastCutoff = DateTime.Now - FastBuildWindow;
        try
        {
            await Parallel.ForEachAsync(objects,
                new ParallelOptions { MaxDegreeOfParallelism = ParallelObjects, CancellationToken = token },
                async (obj, ct) =>
                {
                    try
                    {
                        if (_isFastBuild && new DirectoryInfo(obj.Key).LastWriteTime < fastCutoff)
                            Interlocked.Increment(ref skipped);
                        else
                            await IntegrateObjectAsync(obj.Key, obj.Value, rootFolder, ompPath, channel.Writer, ct);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        await _appLog.LogAsync(LogServiceId.Integration, "access_error", $"Немає доступу: {obj.Key}");
                    }
                    catch (Exception ex)
                    {
                        // Only this object is lost; its pairs already in the channel are still written
                        await _appLog.LogAsync(LogServiceId.Integration, "object_error", $"{obj.Key}: {ex.Message}");
                    }
                    finally
                    {
                        progress?.Report(Interlocked.Increment(ref done) * 100 / objects.Count);
                    }
                });
        }
        finally
        {
            channel.Writer.TryComplete();
            try { await writer; } catch (OperationCanceledException) { }
        }

        if (token.IsCancellationRequested) return false;

        await _appLog.LogAsync(LogServiceId.Integration, "integration_full_done",
            $"Об'єктів: {objects.Count} (пропущено швидкою збіркою: {skipped}), подій: {stats.Pairs}, " +
            $"нових: {stats.Inserted}, доповнено: {stats.Updated}, без об'єкта: {stats.Orphans}, " +
            $"помилок запису: {stats.Failed}, час: {started.Elapsed:hh\\:mm\\:ss}");
        return true;
    }

    private async Task IntegrateObjectAsync(string objectPath, int structId, string rootFolder, string ompPath,
        ChannelWriter<FilePair> output, CancellationToken token)
    {
        var files = new List<BaseFile>();
        foreach (var path in ObjectFiles(objectPath))
        {
            token.ThrowIfCancellationRequested();
            if (IgnoredExtensions.Contains(Path.GetExtension(path))) continue;

            var file = BaseFileFactory.Create(path);
            if (file == null || file.ReconNumber == 0) continue;
            file.StructId = structId;
            files.Add(file);
        }

        var broken = new List<string>();
        foreach (var pair in FilePairing.Pair(files))
        {
            token.ThrowIfCancellationRequested();

            // A pair goes into one month folder, by the RECON's time
            DateTime pairTime = (pair.Data ?? (BaseFile?)pair.Express ?? pair.Other)!.Timestamp;
            foreach (BaseFile? file in new BaseFile?[] { pair.Data, pair.Express, pair.Other })
                if (file != null) await SortFileAsync(file, objectPath, pairTime);

            await CompletePairAsync(pair, rootFolder, ompPath, broken);
            await output.WriteAsync(pair, token);
        }

        if (broken.Count > 0)
            await LogBrokenAsync(objectPath, broken);
    }

    /// <summary>
    /// Files of this object only: its own folder and its month folders. A nested folder is a separate object
    /// (FillStructureAsync registers it) — reading it recursively wrote its files under both objects.
    /// </summary>
    private static IEnumerable<string> ObjectFiles(string objectPath) =>
        Directory.EnumerateFiles(objectPath).Concat(
            Directory.EnumerateDirectories(objectPath)
                .Where(d => MonthFolderRegex.IsMatch(Path.GetFileName(d)))
                .SelectMany(Directory.EnumerateFiles));

    /// <returns>object folder → struct.id (0 if the DB row couldn't be created)</returns>
    private async Task<Dictionary<string, int>> FillStructureAsync(List<string> allDirectories, string rootPath,
        CancellationToken token)
    {
        var objects = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        char sep = Path.DirectorySeparatorChar;

        foreach (var folder in allDirectories)
        {
            token.ThrowIfCancellationRequested();

            var parts = Path.GetRelativePath(rootPath, folder).Split(sep);
            if (parts.Length < 3) continue;

            try
            {
                // root: C:\Data, folder: C:\Data\ОСР\ДТЕКВМ\Вузлова[\2026_11]
                string objectFolderPath = folder;
                if (MonthFolderRegex.IsMatch(Path.GetFileName(folder)))
                {
                    var parent = Directory.GetParent(folder);
                    if (parent == null) continue;
                    objectFolderPath = parent.FullName;
                    Array.Resize(ref parts, parts.Length - 1);
                    if (parts.Length < 3) continue;
                }

                if (objects.ContainsKey(objectFolderPath)) continue;

                int reconNumber = -1;
                foreach (var file in Directory.EnumerateFiles(folder).Take(50))
                {
                    var match = ReconNumRegex.Match(Path.GetFileName(file));
                    if (match.Success && int.TryParse(match.Groups[1].Value, out int rNum))
                    {
                        reconNumber = rNum;
                        break;
                    }
                }
                if (reconNumber == -1) continue;

                string objectName = parts[^1];
                string substationName = parts[^2];
                string unitName = string.Join(" - ", parts.Take(parts.Length - 2));

                objects[objectFolderPath] = await _fileDataRepository.EnsureStructureExistsAsync(
                    unitName, substationName, objectName, reconNumber, objectFolderPath);
            }
            catch (Exception ex)
            {
                await _appLog.LogAsync(LogServiceId.Integration, "file_error", $"Розбір структури {folder}: {ex.Message}");
            }
        }

        return objects;
    }

    // ───────────── pair completion ─────────────

    /// <summary>Generates a missing REXPR, reads file contents and the event data from REXPR.</summary>
    private async Task CompletePairAsync(FilePair pair, string rootFolder, string ompPath, List<string> broken)
    {
        if (pair.Data != null && pair.Express == null)
        {
            pair.Express = await TryGenerateExpressAsync(ompPath, pair.Data);
            if (pair.Express == null) broken.Add(pair.Data.FileName);
        }

        if (pair.Express != null) await pair.Express.ProcessAsync(rootFolder);
        if (pair.Data != null) await pair.Data.ProcessAsync(rootFolder);
        if (pair.Other != null) await pair.Other.ProcessAsync(rootFolder);
    }

    /// <returns>the generated REXPR, or null if OMP_C failed or didn't produce the file</returns>
    private static async Task<ExpressFile?> TryGenerateExpressAsync(string ompPath, DataFile data)
    {
        string dir = Path.GetDirectoryName(data.FullPath)!;
        string expressPath = Path.Combine(dir, "REXPR" + data.FileName[BaseFile.PrefixLength..]);

        if (!File.Exists(expressPath) && !await RunOmpAsync(ompPath, data.FullPath)) return null;
        // Exit code 0 is not enough: without this check the pair got a REXPR with no file → date 0001-01-01
        if (!File.Exists(expressPath)) return null;

        // Generated now, but belongs to the RECON's event: same mtime keeps the pair together in later rebuilds
        try { File.SetLastWriteTime(expressPath, File.GetLastWriteTime(data.FullPath)); } catch { }

        var express = (ExpressFile?)BaseFileFactory.Create(expressPath);
        if (express != null) express.StructId = data.StructId;
        return express;
    }

    private static async Task<bool> RunOmpAsync(string programPath, string inputFilePath)
    {
        using var process = new Process();
        try
        {
            process.StartInfo.FileName = programPath;
            process.StartInfo.Arguments = $"\"{inputFilePath}\" -N";
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;

            if (!process.Start()) return false;
            // Drain the pipes, otherwise a chatty OMP_C blocks on a full buffer and hits the timeout
            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();

            var exited = process.WaitForExitAsync();
            if (await Task.WhenAny(exited, Task.Delay(OmpTimeout)) == exited)
                return process.ExitCode == 0;

            try { process.Kill(true); } catch (InvalidOperationException) { }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private async Task LogBrokenAsync(string where, List<string> broken)
    {
        string sample = string.Join(", ", broken.Take(10)) + (broken.Count > 10 ? " …" : "");
        await _appLog.LogAsync(LogServiceId.Integration, "broken_file",
            $"{where}: RECON без REXPR (OMP_C не зміг згенерувати): {broken.Count} — {sample}");
    }

    // ───────────── DB writing ─────────────

    private sealed class WriteStats
    {
        public int Pairs, Inserted, Updated, Orphans, Failed;
    }

    private async Task WriteFromChannelAsync(ChannelReader<FilePair> reader, WriteStats stats, CancellationToken token)
    {
        var batch = new List<FilePair>(MaxBatchPairs);
        long bytes = 0;
        await foreach (var pair in reader.ReadAllAsync(token))
        {
            batch.Add(pair);
            bytes += (pair.Data?.BinaryData?.Length ?? 0) + (pair.Express?.BinaryData?.Length ?? 0)
                     + (pair.Other?.BinaryData?.Length ?? 0);
            if (batch.Count >= MaxBatchPairs || bytes >= MaxBatchBytes)
            {
                await WriteBatchAsync(batch, stats);
                batch = new List<FilePair>(MaxBatchPairs);
                bytes = 0;
            }
        }
        if (batch.Count > 0) await WriteBatchAsync(batch, stats);
    }

    /// <summary>
    /// A failed batch used to be lost entirely (the files were already moved out of Cache).
    /// Now it is retried row by row, and only the rows that really fail are reported.
    /// </summary>
    private async Task WriteBatchAsync(IReadOnlyList<FilePair> batch, WriteStats stats)
    {
        stats.Pairs += batch.Count;
        try
        {
            await ApplyAsync(await _fileDataRepository.InsertBatchAsync(batch), stats);
        }
        catch (Exception ex)
        {
            await _appLog.LogAsync(LogServiceId.Integration, "batch_error",
                $"Пакет із {batch.Count} подій не записано ({ex.Message}), записуємо по одній");

            foreach (var pair in batch)
            {
                try
                {
                    await ApplyAsync(await _fileDataRepository.InsertBatchAsync([pair]), stats);
                }
                catch (Exception rowEx)
                {
                    stats.Failed++;
                    string files = string.Join(", ", new BaseFile?[] { pair.Data, pair.Express, pair.Other }
                        .Where(f => f != null).Select(f => f!.FullPath));
                    await _appLog.LogAsync(LogServiceId.Integration, "file_error", $"Запис у БД {files}: {rowEx.Message}");
                }
            }
        }
    }

    private async Task ApplyAsync(BatchInsertResult result, WriteStats stats)
    {
        stats.Inserted += result.Inserted;
        stats.Updated += result.Updated;
        stats.Orphans += result.Orphans.Count;
        _statsService.RegisterAction(ServiceType.Integration, result.Inserted + result.Updated);

        if (result.Orphans.Count > 0)
            await _appLog.LogAsync(LogServiceId.Integration, "file_orphan",
                $"Об'єкта немає в struct, не записано: {result.Orphans.Count} — " +
                string.Join(", ", result.Orphans.Take(10)) + (result.Orphans.Count > 10 ? " …" : ""));
    }

    // ───────────── file moves ─────────────

    /// <returns>false if the file stayed where it was</returns>
    private async Task<bool> MoveFileToStorageAsync(BaseFile file, string targetFolder)
    {
        if (!File.Exists(file.FullPath)) return false;
        try
        {
            Directory.CreateDirectory(targetFolder);
            string destPath = Path.Combine(targetFolder, Path.GetFileName(file.FullPath));
            if (!string.Equals(file.FullPath, destPath, StringComparison.OrdinalIgnoreCase))
            {
                RemoveReadOnlyAttribute(file.FullPath);
                if (File.Exists(destPath))
                {
                    RemoveReadOnlyAttribute(destPath);
                    File.Delete(destPath);
                }
                await MoveFileWithRetryAsync(file.FullPath, destPath);
            }
            file.FullPath = destPath;
            file.ParentFolderPath = targetFolder;
            return true;
        }
        catch (Exception ex)
        {
            await _appLog.LogAsync(LogServiceId.Integration, "file_error", $"Не вдалося перемістити {file.FileName}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Moves a file lying directly in the object folder into its month folder (YYYY_MM of <paramref name="time"/>).</summary>
    private async Task SortFileAsync(BaseFile file, string objectRootPath, DateTime time)
    {
        string currentDir = Path.GetDirectoryName(file.FullPath) ?? "";
        if (MonthFolderRegex.IsMatch(Path.GetFileName(currentDir))) return;

        try
        {
            string targetDir = Path.Combine(objectRootPath, FilePairing.MonthFolder(time));
            string targetPath = Path.Combine(targetDir, file.FileName);
            if (string.Equals(file.FullPath, targetPath, StringComparison.OrdinalIgnoreCase)) return;

            Directory.CreateDirectory(targetDir);
            RemoveReadOnlyAttribute(file.FullPath);

            if (File.Exists(targetPath))
            {
                // Same name already there: drop ours only if it is the same file. File numbers repeat,
                // so a different file with this name is another event and must not be deleted.
                var src = new FileInfo(file.FullPath);
                var dst = new FileInfo(targetPath);
                if (src.Length != dst.Length || src.LastWriteTimeUtc != dst.LastWriteTimeUtc)
                {
                    await _appLog.LogAsync(LogServiceId.Integration, "file_warn",
                        $"Сортування {file.FileName}: у {targetDir} вже є інший файл з такою назвою, залишено на місці");
                    return;
                }
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

    private static void RemoveReadOnlyAttribute(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
        catch { /* Ignore if the attribute could not be removed */ }
    }

    private static async Task MoveFileWithRetryAsync(string source, string dest, int maxRetries = 3)
    {
        for (int i = 0; ; i++)
        {
            try
            {
                File.Move(source, dest);
                return;
            }
            catch (IOException) when (i < maxRetries - 1)
            {
                await Task.Delay(500);
            }
            catch (UnauthorizedAccessException) when (i < maxRetries - 1)
            {
                RemoveReadOnlyAttribute(source);
                await Task.Delay(500);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
