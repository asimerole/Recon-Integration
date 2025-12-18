using Recon.Core.Models;

namespace Recon.Core.Factories;

public static class BaseFileFactory
{
    public static BaseFile? Create(string filePath)
    {
        string fileName = Path.GetFileName(filePath).ToUpper();
        
        BaseFile newFile = fileName.Substring(0, 5) switch
        {
            "RECON" => new DataFile(),
            "REXPR" => new ExpressFile(),
            _       => new ReconFile() // Default case для DAILY, RPUSK, RNET і т.д.
        };
        
        newFile.FileName = fileName;
        newFile.FullPath = filePath;
        try 
        {
            newFile.Timestamp = File.GetLastWriteTime(filePath);
        }
        catch
        {
            newFile.Timestamp = DateTime.Now;
        }
        
        newFile.ParseFileNameProperties();
            
        return newFile;
    }
}