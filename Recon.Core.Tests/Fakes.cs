using System.Collections.Concurrent;
using Recon.Core.Application.Interfaces;
using Recon.Core.Application.Interfaces.Repositories;
using Recon.Core.Application.Options;
using Recon.Core.Domain.Enums;
using Recon.Core.Domain.Models;

namespace Recon.Core.Tests;

internal sealed class FakeAppLog : IAppLogRepository
{
    public ConcurrentQueue<(string Action, string? Details)> Entries { get; } = new();

    public Task LogAsync(int serviceId, string actionType, string? details = null, int? userId = null)
    {
        Entries.Enqueue((actionType, details));
        return Task.CompletedTask;
    }

    public Task<List<AppLogEntry>> GetLogsAsync(int limit = 20000) => Task.FromResult(new List<AppLogEntry>());
    public Task CleanupOldLogsAsync(int keepCount = 20000) => Task.CompletedTask;
}

internal sealed class FakeConfig(string storageRoot, OneDriveConfig oneDrive, MailServerConfig? mail = null) : IConfigRepository
{
    public Task<OneDriveConfig> GetOneDriveConfigAsync() => Task.FromResult(oneDrive);
    public Task<string> GetRootFolderAsync() => Task.FromResult(storageRoot);
    public Task<ModuleConfig> GetModuleConfigAsync() => Task.FromResult(new ModuleConfig());
    public Task SaveModuleConfigAsync(ModuleConfig config) => Task.CompletedTask;
    public Task<MailServerConfig> GetMailServerConfigAsync() => Task.FromResult(mail ?? new MailServerConfig());
    public Task<string> GetWinrecPathAsync() => Task.FromResult(string.Empty);
    public Task<int> GetFeedingTimeAsync() => Task.FromResult(10);
}

internal sealed class FakeFileData(List<string> objectFolders) : IFileDataRepository
{
    public Task<List<string>> GetObjectFoldersAsync() => Task.FromResult(objectFolders);

    public Task<BatchInsertResult> InsertBatchAsync(IReadOnlyList<FilePair> batch) => throw new NotSupportedException();
    public Task<string?> GetTargetFolderByReconIdAsync(int reconId) => throw new NotSupportedException();
    public Task<int> EnsureStructureExistsAsync(string unitName, string substationName, string objectName, int reconNumber, string objectFolderPath) => throw new NotSupportedException();
    public Task<List<string>> GetRecipientsByReconIdAsync(int reconId, string? objectName, bool restricted) => throw new NotSupportedException();
    public Task RebuildDatabaseAsync() => throw new NotSupportedException();
}

internal sealed class FakeServers : IServerRepository
{
    public ConcurrentQueue<(int StructId, string Stat)> DailyStats { get; } = new();

    public Task UpdateDailyStatAsync(int serverId, string columnName)
    {
        DailyStats.Enqueue((serverId, columnName));
        return Task.CompletedTask;
    }

    public Task<List<ServerInfo>> GetAllServersAsync() => Task.FromResult(new List<ServerInfo>());
    public Task UpdateServerStatusAsync(int structId, DateTime? lastPing = null, DateTime? lastRecon = null, DateTime? lastDaily = null) => Task.CompletedTask;
    public Task<List<DailyReportRow>> GetDailyReportAsync(DateTime? date = null) => Task.FromResult(new List<DailyReportRow>());
    public Task<bool> TryMarkDailySentAsync(int structId) => Task.FromResult(false);
}

internal sealed class FakeUsers : IUserRepository
{
    public Task<User?> GetUserByLoginAsync(string username) => Task.FromResult<User?>(null);
    public Task<List<string>> GetActiveUserEmailsAsync() => Task.FromResult(new List<string>());
    public Task<List<string>> GetAllUserEmailsAsync() => Task.FromResult(new List<string>());
    public Task<List<string>> GetAdminEmailsAsync() => Task.FromResult(new List<string>());
    public Task<Dictionary<string, List<string>>> GetUsersGroupedBySubstationAsync() => Task.FromResult(new Dictionary<string, List<string>>());
}

internal sealed class FakeOutbox : IMailOutboxRepository
{
    public Task<bool> EnqueueAsync(int reconId, string objectName, string? substation, DateTime eventTime, IEnumerable<string> filePaths) => Task.FromResult(true);
    public Task<List<MailOutboxItem>> GetDueAsync(TimeSpan settle, TimeSpan maxWait) => Task.FromResult(new List<MailOutboxItem>());
    public Task MarkAsync(long id, MailOutboxStatus status, string? recipients = null, string? error = null, TimeSpan? retryAfter = null) => Task.CompletedTask;
    public Task CleanupAsync(int keepDays) => Task.CompletedTask;
}

internal sealed class FakeStats : IStatisticsService
{
    public void RegisterAction(ServiceType type, int count = 1) { }
    public (int Last2Hours, int Today) GetStats(ServiceType type) => (0, 0);
    public void SetDailyCountFromDb(ServiceType type, int dbCount) { }
    public Task<bool> SendAnalyticsToUsers(MailServerConfig config) => Task.FromResult(false);
}
