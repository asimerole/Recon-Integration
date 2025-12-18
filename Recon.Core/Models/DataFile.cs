namespace Recon.Core.Models;

public class DataFile : BaseFile // RECON
{
    protected override async Task ProcessContentSpecificAsync()
    {
        await Task.CompletedTask;
    }
}