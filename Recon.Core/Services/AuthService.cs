using Recon.Core.Infrastructure;
using Recon.Core.Interfaces;
using Recon.Core.Interfaces.Repositories;
using Recon.Core.Options;

namespace Recon.Core.Services;

public class AuthService : IAuthService
{
    private readonly IUserRepository _userRepository;
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ICryptoService _cryptoService;

    public AuthService(IUserRepository userRepository, IDbConnectionFactory connectionFactory, ICryptoService cryptoService)
    {
        _userRepository = userRepository;
        _connectionFactory = connectionFactory;
        _cryptoService = cryptoService;
    }

    public bool Login(string username, string password, DatabaseOptions dbOptions)
    {
        _connectionFactory.SetConnectionString(dbOptions.ConnectionString);

        var user = _userRepository.GetUserByLogin(username);
        if (user == null) return false;

        string hashedPassword = _cryptoService.SHA512(password);
        return string.Equals(hashedPassword, user.PasswordHash, StringComparison.OrdinalIgnoreCase);
    }
}
