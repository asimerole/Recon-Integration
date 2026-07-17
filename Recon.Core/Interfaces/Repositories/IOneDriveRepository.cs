using Recon.Core.Models;

namespace Recon.Core.Interfaces.Repositories;

public interface IOneDriveRepository
{
    Task<List<UserAccessDto>> GetUsersForOneDriveUpdateAsync();
    Task<List<UserAccessDto>> GetUsersForOneDriveRemovalAsync();
    Task MarkOneDriveAccessGrantedAsync(int userId);
    Task MarkOneDriveAccessRevokedAsync(int userId);
}
