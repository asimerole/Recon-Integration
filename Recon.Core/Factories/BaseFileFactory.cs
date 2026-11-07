using Recon.Core.Models;
using System.IO;

namespace Recon.Core.Factories;

public static class BaseFileFactory
{
    public static BaseFile? Create(string filePath)
    {
        string fileName = Path.GetFileName(filePath).ToUpper();
        // Shorter than "RNET353" — not a registrator file (Substring below would throw and abort the whole pass)
        if (fileName.Length < 7) return null;

        BaseFile newFile = fileName switch
        {
            _ when fileName.StartsWith("RECON") => new DataFile(),
            _ when fileName.StartsWith("REXPR") => new ExpressFile(),
            _ => new ReconFile() // DAILY, RPUSK, RNET, DIAGN...
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