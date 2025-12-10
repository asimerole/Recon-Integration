using Recon.Core.Options;

namespace Recon.Core.Interfaces;

public interface IOneDriveService
{
    void CopyToOneDrive(string localSourcePath, string relativePath);

    void StartCleanupScheduler();

    void StopCleanupScheduler();
}