namespace Recon.Core.Interfaces;

public interface ICryptoService
{
    string DecryptConfig(string filePath);
    string SHA512(string input);
}
