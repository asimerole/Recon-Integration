using System.Text.Json;
using Recon.Core.Application.Options;
using Recon.Core.Application.Services;
using static Recon.Core.Application.Services.OneDriveService;

namespace Recon.Core.Tests;

public sealed class OneDriveServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "recon_od_" + Guid.NewGuid().ToString("N"));
    private readonly string _storage;   // root_directory (Data)
    private readonly string _oneDrive;  // onedrive.local_path
    private readonly string _object;    // struct.files_path of one object
    private readonly string _month = DateTime.Now.ToString("yyyy_MM");

    public OneDriveServiceTests()
    {
        _storage = Path.Combine(_root, "DATA");
        _oneDrive = Path.Combine(_root, "OneDrive");
        _object = Path.Combine(_storage, "ОСР", "ДТЕК", "Вузлова");
        Directory.CreateDirectory(Path.Combine(_object, _month));
        Directory.CreateDirectory(_oneDrive);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private (OneDriveService Service, FakeAppLog Log, FakeServers Servers) Create(List<string>? objectFolders = null, int days = 30)
    {
        var config = JsonSerializer.Deserialize<OneDriveConfig>(
            JsonSerializer.Serialize(new { local_path = _oneDrive, days }))!;
        var log = new FakeAppLog();
        var servers = new FakeServers();
        var service = new OneDriveService(log, new FakeConfig(_storage, config),
            new FakeFileData(objectFolders ?? [_object]), servers, new FakeStats())
        {
            RetryDelays = [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(400)],
            CatchUpInterval = TimeSpan.FromHours(1),
        };
        return (service, log, servers);
    }

    private string DataFile(string name, DateTime? eventTime = null, string? month = null)
    {
        string path = Path.Combine(_object, month ?? _month, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, name + Guid.NewGuid());
        File.SetLastWriteTime(path, eventTime ?? DateTime.Now);
        return path;
    }

    private string OneDrivePath(string dataPath) => Path.Combine(_oneDrive, Path.GetRelativePath(_storage, dataPath));

    [Fact]
    public void Copy_keeps_the_path_relative_to_data()
    {
        string src = DataFile("RECON353.300");

        var (result, relative, _) = CopyOnce(new CopyJob(src, _storage, 0), _oneDrive);

        Assert.Equal(CopyResult.Copied, result);
        Assert.Equal(Path.Combine("ОСР", "ДТЕК", "Вузлова", _month, "RECON353.300"), relative);
        Assert.Equal(File.ReadAllText(src), File.ReadAllText(OneDrivePath(src)));
    }

    [Fact]
    public void Second_copy_of_the_same_file_is_skipped()
    {
        string src = DataFile("RECON353.301");
        CopyOnce(new CopyJob(src, _storage, 0), _oneDrive);

        Assert.Equal(CopyResult.UpToDate, CopyOnce(new CopyJob(src, _storage, 0), _oneDrive).Result);
    }

    [Fact]
    public void File_locked_in_onedrive_is_reported_busy()
    {
        string src = DataFile("RECON353.302");
        string dest = OneDrivePath(src);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.WriteAllText(dest, "old");

        using (new FileStream(dest, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Equal(CopyResult.Busy, CopyOnce(new CopyJob(src, _storage, 0), _oneDrive).Result);
    }

    [Fact]
    public void Missing_path_is_not_configured()
    {
        Assert.Equal(CopyResult.NotConfigured, CopyOnce(new CopyJob(DataFile("RECON353.303"), _storage, 0), "").Result);
    }

    [Fact]
    public async Task Locked_file_is_copied_by_retry_after_the_lock_is_released()
    {
        string src = DataFile("RECON353.304");
        string dest = OneDrivePath(src);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.WriteAllText(dest, "old");

        var (service, log, servers) = Create(objectFolders: []);
        service.Start();
        try
        {
            var lockStream = new FileStream(dest, FileMode.Open, FileAccess.Read, FileShare.None);
            service.Enqueue(src, _storage, structId: 7);
            await Task.Delay(250);          // first attempt + first retry hit the lock
            lockStream.Dispose();

            await WaitUntil(() => File.ReadAllText(dest) == File.ReadAllText(src));
            await WaitUntil(() => servers.DailyStats.Contains((7, "uploaded")));
            Assert.Contains(log.Entries, e => e.Action == "onedrive_copied");
        }
        finally
        {
            service.Stop();
        }
    }

    [Fact]
    public async Task Enqueue_while_stopped_is_ignored()
    {
        string src = DataFile("RECON353.305");
        var (service, _, _) = Create();

        service.Enqueue(src, _storage);
        await Task.Delay(200);

        Assert.False(File.Exists(OneDrivePath(src)));
    }

    [Fact]
    public async Task Catch_up_queues_only_missing_files_inside_the_window()
    {
        string fresh = DataFile("RECON353.310");
        string alreadyCopied = DataFile("RECON353.311");
        CopyOnce(new CopyJob(alreadyCopied, _storage, 0), _oneDrive);
        DataFile("RECON353.312", eventTime: DateTime.Now.AddDays(-40));        // older than days
        string oldMonth = DateTime.Now.AddMonths(-3).ToString("yyyy_MM");
        DataFile("RECON353.313", eventTime: DateTime.Now.AddMonths(-3), month: oldMonth); // month outside window: not even read

        string emptyObject = Path.Combine(_storage, "ОСР", "ДТЕК", "Порожній");
        var (service, _, _) = Create(objectFolders: [_object, emptyObject]);

        var (checkedFiles, queued) = await service.CatchUpAsync(CancellationToken.None);

        Assert.Equal(3, checkedFiles);
        Assert.Equal(1, queued);
        Assert.False(File.Exists(OneDrivePath(fresh)));  // only queued: service not started
        // Folder of an object without files exists in OneDrive so Properties can share it
        Assert.True(Directory.Exists(Path.Combine(_oneDrive, "ОСР", "ДТЕК", "Порожній")));
    }

    [Fact]
    public async Task Cleanup_deletes_old_files_but_keeps_folders()
    {
        string oldFile = OneDrivePath(DataFile("RECON353.320"));
        string newFile = OneDrivePath(DataFile("RECON353.321"));
        Directory.CreateDirectory(Path.GetDirectoryName(oldFile)!);
        File.WriteAllText(oldFile, "old");
        File.WriteAllText(newFile, "new");
        File.SetCreationTime(oldFile, DateTime.Now.AddDays(-31));

        var (service, _, _) = Create();
        int deleted = await service.CleanUpOldFilesAsync(_oneDrive, 30);

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(oldFile));
        Assert.True(File.Exists(newFile));

        File.Delete(newFile);
        await service.CleanUpOldFilesAsync(_oneDrive, 30);
        Assert.True(Directory.Exists(Path.GetDirectoryName(oldFile)));   // empty folder stays: sharing is bound to it
    }

    [Fact]
    public async Task Cleanup_with_zero_days_deletes_nothing()
    {
        string file = OneDrivePath(DataFile("RECON353.330"));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "x");
        File.SetCreationTime(file, DateTime.Now.AddYears(-1));

        var (service, _, _) = Create();

        Assert.Equal(0, await service.CleanUpOldFilesAsync(_oneDrive, 0));
        Assert.True(File.Exists(file));
    }

    [Theory]
    [InlineData("2026_10", "2026-10-20", true)]
    [InlineData("2026_09", "2026-09-20", true)]   // month ends after cutoff
    [InlineData("2026_08", "2026-09-01", false)]  // month ended exactly at cutoff
    [InlineData("2026_13", "2026-01-01", false)]
    [InlineData("Cache", "2026-01-01", false)]
    public void Month_folder_window(string folder, string cutoff, bool expected)
    {
        Assert.Equal(expected, IsMonthInWindow(folder, DateTime.Parse(cutoff)));
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var until = DateTime.Now.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.Now > until) throw new TimeoutException("condition not met");
            await Task.Delay(50);
        }
    }
}
