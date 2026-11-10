namespace Recon.Core.Domain.Models;

public class AppLogEntry
{
    public int      Id         { get; set; }
    public int      ServiceId  { get; set; }
    public string   ActionType { get; set; } = string.Empty;
    public string?  Details    { get; set; }
    public DateTime CreatedAt  { get; set; }

    public string ServiceName => ServiceId switch
    {
        1 => "FTP",
        2 => "Інтеграція",
        3 => "Пошта",
        4 => "OneDrive",
        _ => "Система"
    };

    public bool IsError   => ActionType.Contains("error", StringComparison.OrdinalIgnoreCase)
                          || ActionType.Contains("fail",  StringComparison.OrdinalIgnoreCase);
    public bool IsWarning => ActionType.Contains("warn",  StringComparison.OrdinalIgnoreCase);
}
