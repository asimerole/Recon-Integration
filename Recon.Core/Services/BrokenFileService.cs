using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Recon.Core.Interfaces;

namespace Recon.Core.Services;

public class BrokenFileService
{
    private readonly ConcurrentBag<BrokenFileEntry> _errors = new();
    private readonly ILogger<IIntegrationService> _logger;
    
    private readonly string _logFilePath;

    public BrokenFileService(ILogger<IIntegrationService> logger)
    {
        _logger = logger;
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string folder = Path.Combine(appData, "ReconIntegrationLog");

        if (!Directory.Exists(folder))
        {
            Directory.CreateDirectory(folder);
        }

        _logFilePath = Path.Combine(folder, "broken_files.json");
    }

    /// <summary>
    /// Adds an error to memory. This is very fast and does not block threads.
    /// </summary>
    public void LogBrokenFile(string filePath, string status)
    {
        _errors.Add(new BrokenFileEntry 
        { 
            FilePath = filePath, 
            Status = status,
            Timestamp = DateTime.Now 
        });
    }

    /// <summary>
    /// Saves accumulated errors to disk.
    /// Call at the end of processing or periodically.
    /// </summary>
    public async Task SaveLogAsync()
    {
        if (_errors.IsEmpty) return;

        try
        {
            List<BrokenFileEntry> existingLogs;
            
            if (File.Exists(_logFilePath))
            {
                string json = await File.ReadAllTextAsync(_logFilePath);
                try
                {
                    existingLogs = JsonSerializer.Deserialize<List<BrokenFileEntry>>(json) 
                                   ?? new List<BrokenFileEntry>();
                }
                catch
                {
                    existingLogs = new List<BrokenFileEntry>();
                }
            }
            else
            {
                existingLogs = new List<BrokenFileEntry>();
            }
            
            existingLogs.AddRange(_errors);
            
            var options = new JsonSerializerOptions { WriteIndented = true };
            await File.WriteAllTextAsync(_logFilePath, JsonSerializer.Serialize(existingLogs, options));
            
            _errors.Clear();
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to save broken files log: {ex.Message}");
        }
    }
}

public class BrokenFileEntry
{
    public string FilePath { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
}