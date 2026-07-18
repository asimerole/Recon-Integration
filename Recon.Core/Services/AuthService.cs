using Recon.Core.Infrastructure;
using Recon.Core.Interfaces;
using Recon.Core.Interfaces.Repositories;
using Recon.Core.Options;

namespace Recon.Core.Services;

public class AuthService : IAuthService
{
    private readonly IUserRepository _userRepository;
    private readonly IDbConnectionFactory _dbFactory;
    private readonly ICryptoService _crypto;

    public AuthService(IUserRepository userRepository, IDbConnectionFactory dbFactory, ICryptoService crypto)
    {
        _userRepository = userRepository;
        _dbFactory = dbFactory;
        _crypto = crypto;
    }

    public async Task<bool> LoginAsync(string username, string password, DatabaseOptions dbOptions)
    {
        _dbFactory.SetConnectionString(dbOptions.ConnectionString);

        var user = await _userRepository.GetUserByLoginAsync(username);
        if (user == null) return false;

        return _crypto.SHA512(password) == user.PasswordHash.ToUpper();
    }
}
