namespace Recon.Core.Models;

public class UserAccessDto
{
    public int UserId { get; set; }
    public string Email { get; set; } 
    public List<string> FolderPaths { get; set; } = new();
    
    public bool IsAdmin { get; set; }
}