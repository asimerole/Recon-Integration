using Recon.Core.Dtos;
using Recon.Core.Infrastructure;
using Recon.Core.Interfaces;
using Recon.Core.Interfaces.Repositories;

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

    public async Task<bool> LoginAsync(string username, string password, DbConnectionParamsDto dbOptions)
    {
        _connectionFactory.Initialize(dbOptions);

        var user = await _userRepository.GetUserByLoginAsync(username);
        if (user == null) return false;

        string hashedPassword = _cryptoService.SHA512(password);
        return string.Equals(hashedPassword, user.PasswordHash, StringComparison.OrdinalIgnoreCase);
    }
}
