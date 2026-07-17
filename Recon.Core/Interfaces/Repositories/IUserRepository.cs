using Recon.Core.Models;

namespace Recon.Core.Interfaces.Repositories;

public interface IUserRepository
{
    User? GetUserByLogin(string username);
    List<string> GetActiveUserEmails();
    List<string> GetAllUserEmails();
    Dictionary<string, List<string>> GetUsersGroupedBySubstation();
}
