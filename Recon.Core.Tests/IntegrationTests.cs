using System.Data;
using System.Text;
using Recon.Core.Application.Services;
using Recon.Core.Domain.Factories;
using Recon.Core.Domain.Models;
using Recon.Core.Infrastructure.Repositories;

namespace Recon.Core.Tests;

public sealed class FilePairingTests : IDisposable
{
    private readonly string _object = Path.Combine(Path.GetTempPath(), "recon_pair_" + Guid.NewGuid().ToString("N"));

    public FilePairingTests() => Directory.CreateDirectory(_object);

    public void Dispose()
    {
        try { Directory.Delete(_object, true); } catch { }
    }

    private BaseFile File(string month, string name, DateTime mtime)
    {
        string path = Path.Combine(_object, month, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, name);
        System.IO.File.SetLastWriteTime(path, mtime);
        return BaseFileFactory.Create(path)!;
    }

    [Fact]
    public void Pair_across_month_boundary_is_kept_together()
    {
        // The old key "recon.num.yyyyMM" put these into different pairs
        var pairs = FilePairing.Pair([
            File("2026_10", "RECON353.300", new DateTime(2026, 10, 31, 23, 59, 50)),
            File("2026_11", "REXPR353.300", new DateTime(2026, 11, 1, 0, 0, 5)),
        ]);

        var pair = Assert.Single(pairs);
        Assert.NotNull(pair.Data);
        Assert.NotNull(pair.Express);
    }

    [Fact]
    public void Generated_rexpr_in_the_same_folder_pairs_whatever_its_mtime()
    {
        // OMP_C ran a week later: mtime = generation time
        var pairs = FilePairing.Pair([
            File("2026_10", "RECON353.300", new DateTime(2026, 10, 3, 12, 0, 0)),
            File("2026_10", "REXPR353.300", new DateTime(2026, 11, 7, 9, 0, 0)),
        ]);

        Assert.True(Assert.Single(pairs).IsComplete);
    }

    [Fact]
    public void Same_file_number_in_different_years_is_two_events()
    {
        var pairs = FilePairing.Pair([
            File("2024_03", "RECON353.300", new DateTime(2024, 3, 1)),
            File("2024_03", "REXPR353.300", new DateTime(2024, 3, 1)),
            File("2025_06", "RECON353.300", new DateTime(2025, 6, 9)),
            File("2025_06", "REXPR353.300", new DateTime(2025, 6, 9)),
        ]);

        Assert.Equal(2, pairs.Count);
        Assert.All(pairs, p => Assert.True(p.IsComplete));
        Assert.All(pairs, p => Assert.Equal(Path.GetDirectoryName(p.Data!.FullPath), Path.GetDirectoryName(p.Express!.FullPath)));
    }

    [Fact]
    public void Far_apart_files_in_different_folders_are_not_paired()
    {
        var pairs = FilePairing.Pair([
            File("2026_01", "RECON353.300", new DateTime(2026, 1, 10)),
            File("2026_05", "REXPR353.300", new DateTime(2026, 5, 10)),
        ]);

        Assert.Equal(2, pairs.Count);
        Assert.Contains(pairs, p => p.Data != null && p.Express == null);
        Assert.Contains(pairs, p => p.Express != null && p.Data == null);
    }

    [Fact]
    public void Other_files_are_single_records()
    {
        var pairs = FilePairing.Pair([
            File("2026_11", "DAILY353.001", new DateTime(2026, 11, 1)),
            File("2026_11", "RNET_353.002", new DateTime(2026, 11, 2)),
        ]);

        Assert.Equal(2, pairs.Count);
        Assert.All(pairs, p => Assert.NotNull(p.Other));
    }
}

public class FilePairTests
{
    private static ExpressFile Express(DateTime time, bool parsed)
    {
        var e = new ExpressFile { FileName = "REXPR353.300", Timestamp = time };
        if (parsed)
        {
            // HasParsedTimestamp is set only by parsing real content
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            e.BinaryData = Encoding.GetEncoding(866).GetBytes(
                $"Дата: {time.ToString("dd/MM/yyyy", inv)}\nВремя: {time.ToString("HH:mm:ss.fff", inv)}\n");
            typeof(ExpressFile).GetMethod("ProcessContentSpecificAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(e, null);
        }
        return e;
    }

    static FilePairTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    [Fact]
    public void Event_time_comes_from_parsed_rexpr()
    {
        var eventTime = new DateTime(2026, 11, 3, 14, 25, 7, 120);
        var pair = new FilePair
        {
            Data = new DataFile { Timestamp = new DateTime(2026, 11, 3, 14, 26, 0) },
            Express = Express(eventTime, parsed: true),
        };

        Assert.Equal(eventTime, pair.Timestamp);
    }

    [Fact]
    public void Unparsed_rexpr_falls_back_to_recon_mtime_not_generation_time()
    {
        var reconTime = new DateTime(2026, 11, 3, 14, 26, 0);
        var pair = new FilePair
        {
            Data = new DataFile { Timestamp = reconTime },
            Express = Express(new DateTime(2026, 11, 7, 9, 0, 0), parsed: false),   // generated a week later
        };

        Assert.Equal(reconTime, pair.Timestamp);
    }

    [Theory]
    [InlineData(true, true, null, "RECON")]
    [InlineData(true, false, null, "RECON")]
    [InlineData(false, true, null, "REXPR")]
    [InlineData(false, false, "RNET_", "RNET_")]
    [InlineData(false, false, "DAILY", "DAILY")]
    public void File_type_matches_what_viewer_expects(bool data, bool express, string? otherPrefix, string expected)
    {
        var pair = new FilePair
        {
            Data = data ? new DataFile() : null,
            Express = express ? new ExpressFile() : null,
            Other = otherPrefix != null ? new ReconFile { FilePrefix = otherPrefix } : null,
        };

        Assert.Equal(expected, pair.FileType);
    }

    [Fact]
    public void StructId_is_taken_from_whichever_file_has_it()
    {
        var pair = new FilePair { Data = new DataFile(), Express = new ExpressFile { StructId = 42 } };
        Assert.Equal(42, pair.StructId);
    }
}

public class ImportTableTests
{
    [Fact]
    public void Pair_without_usable_time_is_not_written_as_0001_01_01()
    {
        // OMP_C "succeeded" but produced nothing: used to become a row dated 0001-01-01
        var table = FileDataRepository.BuildImportTable([new FilePair { Express = new ExpressFile { FileNum = "300" } }]);
        Assert.Equal(0, table.Rows.Count);
    }

    [Fact]
    public void Row_carries_struct_type_family_and_event_time()
    {
        var time = new DateTime(2026, 11, 3, 14, 25, 7);
        var pair = new FilePair
        {
            Data = new DataFile { ReconNumber = 353, FileNum = "300", Object = "Вузлова", Timestamp = time, StructId = 7, BinaryData = [1] },
        };

        DataRow row = FileDataRepository.BuildImportTable([pair]).Rows[0];

        Assert.Equal(7, row["StructId"]);
        Assert.Equal("RECON", row["FileType"]);
        Assert.Equal("RECON", row["Family"]);
        Assert.Equal(time.Date, row["Date"]);
        Assert.Equal(time.TimeOfDay, row["Time"]);
        Assert.Equal(false, row["HasExpress"]);
        Assert.Equal(1L, row["DataSize"]);          // size separates a re-delivery from a reset number
        Assert.Equal(DBNull.Value, row["OtherSize"]);
    }

    [Fact]
    public void Unknown_struct_is_null_so_sql_resolves_one_struct()
    {
        var pair = new FilePair { Other = new ReconFile { FilePrefix = "DAILY", FileNum = "001", Timestamp = DateTime.Now } };

        DataRow row = FileDataRepository.BuildImportTable([pair]).Rows[0];

        Assert.Equal(DBNull.Value, row["StructId"]);
        Assert.Equal("DAILY", row["Family"]);
    }
}
