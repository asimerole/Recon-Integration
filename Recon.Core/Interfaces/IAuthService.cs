using Recon.Core.Options;

namespace Recon.Core.Interfaces;

public interface IAuthService
{
    Task<bool> LoginAsync(string username, string password, DatabaseOptions dbOptions);
}
