using System.Security.Cryptography;
using System.Text;
using System.Windows;
using Recon.Core.Interfaces;
using Recon.Core.Options;

namespace Recon.Core.Services;

public class AuthService : IAuthService
{
    private readonly IDatabaseService _dbService;

    public AuthService(IDatabaseService dbService)
    {
        _dbService = dbService;
    }
    public bool Login(string username, string password, DatabaseOptions dbOptions)
    {
        _dbService.Initialize(dbOptions.ConnectionString);
        
        var user = _dbService.GetUserByLogin(username);

        if (user != null)
        {
            var cryptoService = new CryptoService();
            var hashedPassword = cryptoService.SHA512(password);

            if (hashedPassword == user.PasswordHash.ToUpper())
            {
                return true;
            }
        }
        
        MessageBox.Show("Користувача не було знайдено.","Увага!");
        return false;
    }
}