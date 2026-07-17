using Recon.Core.Dtos;

namespace Recon.Core.Interfaces;

public interface IAuthService
{
    Task<bool> LoginAsync(string username, string password, DbConnectionParamsDto dbOptions);
}
