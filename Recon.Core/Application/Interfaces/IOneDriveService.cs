namespace Recon.Core.Application.Interfaces;

public interface IOneDriveService
{
    bool IsRunning { get; }

    /// <summary>Starts the copy queue, catch-up of missed files and nightly cleanup.</summary>
    void Start();

    void Stop();

    /// <summary>
    /// Queues a file that already sits in the storage (root_directory) for copying into the OneDrive folder
    /// under the same relative path. Returns immediately; ignored while the module is stopped.
    /// </summary>
    void Enqueue(string sourceFullPath, string storageRoot, int structId = 0);
}
