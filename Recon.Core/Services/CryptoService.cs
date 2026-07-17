using Recon.Core.Interfaces;
using System.Security.Cryptography;
using System.Text;
using System.IO;
using Recon.Core.Keys;

namespace Recon.Core.Services;

public class CryptoService : ICryptoService
{
    private static readonly byte[] Key = Secrets.AesKey;
    private static readonly byte[] IV = Secrets.AesIV;
    
    public string DecryptConfig(string fullPath)
    {
        var fileBytes = File.ReadAllBytes(fullPath);

        byte[] fileIv = new byte[16];
        Array.Copy(fileBytes, 0, fileIv, 0, 16);

        byte[] encryptedBytes = new byte[fileBytes.Length - 16];
        Array.Copy(fileBytes, 16, encryptedBytes, 0, encryptedBytes.Length);

        using (var aes = Aes.Create())
        {
            aes.Key = Key; 
            aes.IV = fileIv; 
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using (ICryptoTransform decryptor = aes.CreateDecryptor(aes.Key, aes.IV))
            {
                byte[] decryptedBytes = decryptor.TransformFinalBlock(encryptedBytes, 0, encryptedBytes.Length);
                return Encoding.UTF8.GetString(decryptedBytes);
            }
        }
    }

    public string SHA512(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        using (var hash = System.Security.Cryptography.SHA512.Create())
        {
            var hashedInputBytes = hash.ComputeHash(bytes);

            // Convert to text
            // StringBuilder Capacity is 128, because 512 bits / 8 bits in byte * 2 symbols for byte 
            var hashedInputStringBuilder = new StringBuilder(128);
            foreach (var b in hashedInputBytes)
                hashedInputStringBuilder.Append(b.ToString("X2"));
            return hashedInputStringBuilder.ToString();
        }
    }
}