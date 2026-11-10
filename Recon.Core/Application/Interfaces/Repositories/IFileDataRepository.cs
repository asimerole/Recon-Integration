using Recon.Core.Domain.Models;

namespace Recon.Core.Application.Interfaces.Repositories;

public interface IFileDataRepository
{
    /// <summary>
    /// Writes events into data/data_process. An event that is already there (same object and file number,
    /// same file family, within 24 h) is completed instead of inserted again.
    /// </summary>
    Task<BatchInsertResult> InsertBatchAsync(IReadOnlyList<FilePair> batch);

    Task<string?> GetTargetFolderByReconIdAsync(int reconId);

    /// <returns>struct.id of the object (0 if it couldn't be created)</returns>
    Task<int> EnsureStructureExistsAsync(string unitName, string substationName, string objectName, int reconNumber, string objectFolderPath);

    Task<List<string>> GetRecipientsByReconIdAsync(int reconId, string? objectName, bool restricted);
    // struct.files_path of every object (sp_GetObjectFolders)
    Task<List<string>> GetObjectFoldersAsync();
    Task RebuildDatabaseAsync();
}

/// <param name="Orphans">"recon/object/file" of rows whose object is missing in struct (not written)</param>
public record BatchInsertResult(int Inserted, int Updated, IReadOnlyList<string> Orphans);
