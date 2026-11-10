using Recon.Core.Application.Services;
using Recon.Core.Domain.Models;

namespace Recon.Core.Tests;

public class FtpFileGateTests
{
    private static readonly DateTime T = new(2026, 11, 7, 10, 0, 0);

    [Fact]
    public void File_is_taken_only_on_the_second_identical_listing()
    {
        var gate = new FtpFileGate();

        gate.BeginCycle();
        Assert.False(gate.IsStable("ip|/RECON353.300", 100, T));
        gate.EndCycle();

        gate.BeginCycle();
        Assert.True(gate.IsStable("ip|/RECON353.300", 100, T));
    }

    [Fact]
    public void Growing_file_waits()
    {
        var gate = new FtpFileGate();

        gate.BeginCycle();
        gate.IsStable("k", 100, T);
        gate.EndCycle();

        gate.BeginCycle();
        Assert.False(gate.IsStable("k", 250, T.AddSeconds(5)));   // still being written
        gate.EndCycle();

        gate.BeginCycle();
        Assert.True(gate.IsStable("k", 250, T.AddSeconds(5)));
    }

    [Fact]
    public void Files_not_listed_in_a_cycle_are_forgotten()
    {
        var gate = new FtpFileGate();

        gate.BeginCycle();
        gate.IsStable("gone", 1, T);
        gate.IsStable("stays", 1, T);
        gate.EndCycle();

        gate.BeginCycle();
        gate.IsStable("stays", 1, T);
        gate.EndCycle();

        Assert.Equal(1, gate.Count);
    }
}

public class ServerFailureTrackerTests
{
    private static readonly DateTime T = new(2026, 11, 7, 10, 0, 0);

    [Fact]
    public void Dead_server_is_logged_once_then_hourly_then_on_recovery()
    {
        var tracker = new ServerFailureTracker();

        Assert.Equal(ServerFailureTracker.Decision.FirstFailure, tracker.OnFailure("s", T));
        Assert.Equal(ServerFailureTracker.Decision.Silent, tracker.OnFailure("s", T.AddMinutes(10)));
        Assert.Equal(ServerFailureTracker.Decision.Reminder, tracker.OnFailure("s", T.AddMinutes(61)));
        Assert.Equal(ServerFailureTracker.Decision.Silent, tracker.OnFailure("s", T.AddMinutes(70)));

        Assert.Equal(TimeSpan.FromMinutes(90), tracker.OnSuccess("s", T.AddMinutes(90)));
        Assert.Null(tracker.OnSuccess("s", T.AddMinutes(91)));   // healthy: no "restored" spam
    }
}

public class FtpFolderTests
{
    [Fact]
    public void Unit_is_split_only_on_the_integration_separator()
    {
        var server = new ServerInfo { Unit = "ОСР - Південно-Західне ТУ", Substation = "ДТЕК", Object = "Вузлова" };

        Assert.Equal(Path.Combine("ОСР", "Південно-Західне ТУ", "ДТЕК", "Вузлова"), FtpService.DirTreeOf(server));
    }

    [Fact]
    public void Invalid_characters_are_replaced()
    {
        var server = new ServerInfo { Unit = "ОСР", Substation = "ПС 330/110", Object = "" };

        Assert.Equal(Path.Combine("ОСР", "ПС 330_110", "Unknown"), FtpService.DirTreeOf(server));
    }
}
