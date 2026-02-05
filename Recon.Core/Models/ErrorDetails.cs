namespace Recon.Core.Models;

public class ErrorDetails
{
    public string FullName { get; set; }
    public string ErrorMessage { get; set; }

    public ErrorDetails(string name, string error)
    {
        FullName = name;
        ErrorMessage = error;
    }
}