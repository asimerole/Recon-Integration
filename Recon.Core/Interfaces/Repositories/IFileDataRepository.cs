using Recon.Core.Models;

namespace Recon.Core.Interfaces.Repositories;

public interface IFileDataRepository
{
    Task InsertBatchAsync(List<FilePair> batch);
    Task<string?> GetTargetFolderByReconIdAsync(int reconId);
    Task EnsureStructureExistsAsync(string unitName, string substationName, string objectName, int reconNumber, string objectFolderPath);
    Task<List<string>> GetRecipientsByReconIdAsync(int reconId, string? objectName = null);
    Task RebuildDatabaseAsync();
}
