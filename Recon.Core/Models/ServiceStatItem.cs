namespace Recon.Core.Models;

public class ServiceStatItem
{
    public string ServiceName { get; set; } // Назва (SQL, FTP...)
    public int Last2Hours { get; set; }     // За 2 години
    public int Today { get; set; }          // За добу
    
    // Можна додати статус (Online/Error)
    public string Status { get; set; } = "OK"; 
}