namespace Recon.Core.Interfaces;

public interface IOneDriveService
{
    /// <summary>
    /// Copies a file that already sits in the storage (root_directory) into the OneDrive folder,
    /// keeping the same relative path. Returns true if the file was copied.
    /// </summary>
    Task<bool> CopyFromStorageAsync(string sourceFullPath, string storageRoot);

    void StartCleanupScheduler();

    void StopCleanupScheduler();
}
