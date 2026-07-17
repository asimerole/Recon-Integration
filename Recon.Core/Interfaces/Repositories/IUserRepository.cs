using Recon.Core.Models;

namespace Recon.Core.Interfaces.Repositories;

public interface IUserRepository
{
    Task<User?> GetUserByLoginAsync(string username);
    Task<List<string>> GetActiveUserEmailsAsync();
    Task<List<string>> GetAllUserEmailsAsync();
    Task<Dictionary<string, List<string>>> GetUsersGroupedBySubstationAsync();
}
