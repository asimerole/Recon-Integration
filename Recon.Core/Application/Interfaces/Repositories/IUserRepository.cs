using Recon.Core.Domain.Models;

namespace Recon.Core.Application.Interfaces.Repositories;

public interface IUserRepository
{
    Task<User?> GetUserByLoginAsync(string username);
    Task<List<string>> GetActiveUserEmailsAsync();
    Task<List<string>> GetAllUserEmailsAsync();
    Task<List<string>> GetAdminEmailsAsync();
    Task<Dictionary<string, List<string>>> GetUsersGroupedBySubstationAsync();
}
