using System.Collections.Concurrent;

namespace Recon.Core.Application.Services;

/// <summary>
/// A file is downloaded only after two listings in a row show the same size and modified time.
/// Protects against taking a file the registrator is still writing — FTP deletes it on the device
/// right after the download, so a half-written file would be lost for good.
/// </summary>
internal sealed class FtpFileGate
{
    private readonly ConcurrentDictionary<string, (long Size, DateTime Modified, int Cycle)> _seen = new();
    private int _cycle;

    public void BeginCycle() => Interlocked.Increment(ref _cycle);

    public bool IsStable(string key, long size, DateTime modified)
    {
        int cycle = Volatile.Read(ref _cycle);
        bool stable = _seen.TryGetValue(key, out var prev) && prev.Size == size && prev.Modified == modified;
        _seen[key] = (size, modified, cycle);
        return stable;
    }

    public void Forget(string key) => _seen.TryRemove(key, out _);

    /// <summary>Drops files that were not listed in this cycle (taken by someone else, deleted on the device).</summary>
    public void EndCycle()
    {
        int cycle = Volatile.Read(ref _cycle);
        foreach (var (key, value) in _seen)
            if (value.Cycle != cycle) _seen.TryRemove(key, out _);
    }

    public int Count => _seen.Count;
}

/// <summary>
/// Dead servers are polled every cycle; logging each failure would flood the 20k-row journal.
/// Logs the first failure, a reminder every <see cref="RemindEvery"/>, and the recovery.
/// </summary>
internal sealed class ServerFailureTracker
{
    public static readonly TimeSpan RemindEvery = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, (DateTime Since, DateTime LastLogged)> _failing = new();

    public enum Decision { Silent, FirstFailure, Reminder }

    public Decision OnFailure(string server, DateTime now)
    {
        if (!_failing.TryGetValue(server, out var state))
        {
            _failing[server] = (now, now);
            return Decision.FirstFailure;
        }
        if (now - state.LastLogged < RemindEvery) return Decision.Silent;
        _failing[server] = (state.Since, now);
        return Decision.Reminder;
    }

    /// <returns>how long the server was down, or null if it was fine</returns>
    public TimeSpan? OnSuccess(string server, DateTime now) =>
        _failing.TryRemove(server, out var state) ? now - state.Since : null;

    public DateTime? DownSince(string server) => _failing.TryGetValue(server, out var s) ? s.Since : null;
}
