using Recon.Core.Options;

namespace Recon.Core.Interfaces;

public interface IAuthService
{
    bool Login(string username, string passwordHash, DatabaseOptions dbOptions);
    
}