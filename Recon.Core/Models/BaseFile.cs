namespace Recon.Core.Models;

public abstract class BaseFile
{
    public string FileName { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public string ParentFolderPath { get; set; } = string.Empty;
    
    public string FileNum { get; set; } = string.Empty; // xxxxx.xxx.730
    public int ReconNumber { get; set; }                // xxxxx.730.xxx
    public string FilePrefix { get; set; } = string.Empty;
    
    public string Unit { get; set; } = string.Empty;
    public string Substation { get; set; } = string.Empty;
    public string Object { get; set; } = string.Empty;
    public int ServerId { get; set; }
    
    public DateTime Timestamp { get; set; }
    
    public byte[]? BinaryData { get; set; }

    protected abstract Task ProcessContentSpecificAsync();
    
    public async Task ProcessAsync(string rootFolder)
    {
        if (File.Exists(FullPath))
        {   
            BinaryData = await File.ReadAllBytesAsync(FullPath);
            Timestamp = File.GetLastWriteTime(FullPath);
            
            ParentFolderPath = Path.GetDirectoryName(FullPath) ?? "";
            
            ProcessFilePath(rootFolder);
            
            await ProcessContentSpecificAsync();
        }
    }
    
    private void ProcessFilePath(string rootFolder)
    {
        string currentParentPath = ParentFolderPath; 
        
        string parentFolderName = Path.GetFileName(currentParentPath);
        
        string objectPath = currentParentPath;

        if (IsSortedFolder(parentFolderName))
        {
            DirectoryInfo? parentDir = Directory.GetParent(currentParentPath);
        
            if (parentDir != null)
            {
                objectPath = parentDir.FullName;
            }
        }
    
        
        ParentFolderPath = objectPath; 
        
        string relativePath = Path.GetRelativePath(rootFolder, objectPath);
        
        char separator = Path.DirectorySeparatorChar;
        var pathParts = relativePath.Split(separator, StringSplitOptions.RemoveEmptyEntries).ToList();
        
        if (pathParts.Count >= 3)
        {
            Object = pathParts.Last(); 
            pathParts.RemoveAt(pathParts.Count - 1); 
            
            Substation = pathParts.Last(); 
            pathParts.RemoveAt(pathParts.Count - 1); 
            
            Unit = string.Join(" - ", pathParts);
        }
    }

    private bool IsSortedFolder(string folderName)
    {
        try
        {
            if (folderName.Length == 7 && folderName[4] == '_')
            {
                var year = folderName.Substring(0, 4);
                var month = folderName.Substring(5, 2);

                return int.TryParse(year, out _) && int.TryParse(month, out _);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
            throw;
        }

        return false;
    }
    
    protected internal void ParseFileNameProperties()
    {
        if (FileName.Length >= 8) 
        {
            FilePrefix = FileName.Substring(0, 5); 
            FileNum = FileName.Substring(FileName.Length - 3); 
        
            if (int.TryParse(FileName.Substring(5, 3), out int reconNum))
            {
                ReconNumber = reconNum;
            }
            else
            {
                ReconNumber = 0;
            }
            
        }
    }
}