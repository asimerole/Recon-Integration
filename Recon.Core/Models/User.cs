namespace Recon.Core.Models;

public class User
{
    public string Username { get; set; } = String.Empty;
    
    public string PasswordHash { get; set; } = String.Empty;
}