using Recon.Core.Domain.Factories;
using Recon.Core.Domain.Models;

namespace Recon.Core.Tests;

public class FileNameParsingTests
{
    [Theory]
    [InlineData("RECON353.300", typeof(DataFile), "RECON", 353, "300")]
    [InlineData("REXPR353.300", typeof(ExpressFile), "REXPR", 353, "300")]
    [InlineData("DAILY720.001", typeof(ReconFile), "DAILY", 720, "001")]
    [InlineData("RPUSK158.802", typeof(ReconFile), "RPUSK", 158, "802")]
    [InlineData("DIAGN331.140", typeof(ReconFile), "DIAGN", 331, "140")]
    // Viewer filters file_type = 'RNET_': the underscore is part of the 5-char prefix
    [InlineData("RNET_353.300", typeof(ReconFile), "RNET_", 353, "300")]
    [InlineData("recon353.300", typeof(DataFile), "RECON", 353, "300")]
    public void Parses_prefix_recon_number_and_file_number(string name, Type type, string prefix, int recon, string fileNum)
    {
        var file = BaseFileFactory.Create(Path.Combine(Path.GetTempPath(), name))!;

        Assert.IsType(type, file);
        Assert.Equal(prefix, file.FilePrefix);
        Assert.Equal(recon, file.ReconNumber);
        Assert.Equal(fileNum, file.FileNum);
    }

    [Theory]
    [InlineData("abc")]      // used to throw in Substring and abort the whole Cache pass
    [InlineData("RECON1")]
    public void Too_short_name_is_not_a_registrator_file(string name)
    {
        Assert.Null(BaseFileFactory.Create(Path.Combine(Path.GetTempPath(), name)));
    }

    [Fact]
    public void Foreign_file_gets_recon_zero_so_it_is_parked_not_processed()
    {
        var file = BaseFileFactory.Create(Path.Combine(Path.GetTempPath(), "Thumbs.db"))!;
        Assert.Equal(0, file.ReconNumber);
    }
}
