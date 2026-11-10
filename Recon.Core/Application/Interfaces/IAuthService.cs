using Recon.Core.Application.Dtos;

namespace Recon.Core.Application.Interfaces;

public interface IAuthService
{
    Task<bool> LoginAsync(string username, string password, DbConnectionParamsDto dbOptions);
}
