using Azure.Identity;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Drives.Item.Items.Item.Invite;
using Microsoft.Graph.Models.ODataErrors;
using Recon.Core.Interfaces.Repositories;
using Microsoft.Extensions.Logging;
using Recon.Core.Models;
using Recon.Core.Options;
using System.IO;

namespace Recon.Core.Services;

public class OneDrivePermissonService
{
    private GraphServiceClient? _graphClient;

    private string _cloudRootFolderName = string.Empty;
    private string _localRootFullPath = string.Empty;
    private string _adminEmail = string.Empty;
    private AzureConfig? _currentConfig;

    private Task? _workingTask;
    private CancellationTokenSource? _cts;

    private readonly IOneDriveRepository _oneDriveRepository;
    private readonly IConfigRepository _configRepository;
    private readonly ILogger<OneDrivePermissonService> _logger;

    public OneDrivePermissonService(IOneDriveRepository oneDriveRepository, IConfigRepository configRepository,
        ILogger<OneDrivePermissonService> logger)
    {
        _oneDriveRepository = oneDriveRepository;
        _configRepository = configRepository;
        _logger = logger;
    }

    public void StartMonitoring()
    {
        if (_workingTask != null && !_workingTask.IsCompleted) return;

        _cts = new CancellationTokenSource();
        _workingTask = Task.Run(() => WorkerLoop(_cts.Token));
        _logger.LogInformation("OneDrive Permission Monitor запущено.");
    }

    public void StopMonitoring()
    {
        _cts?.Cancel();
    }

    private async Task WorkerLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var azureConfig = await _configRepository.GetAzureConfigAsync();
                if (azureConfig == null)
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), token);
                    continue;
                }

                var oneDriveConfig = await _configRepository.GetOneDriveConfigAsync();
                var sourceRootPath = await _configRepository.GetRootFolderAsync();

                Initialize(azureConfig, oneDriveConfig.Path, sourceRootPath);

                var usersToGrant = await _oneDriveRepository.GetUsersForOneDriveUpdateAsync();
                foreach (var user in usersToGrant)
                {
                    if (token.IsCancellationRequested) break;
                    var paths = await SyncUserPermissionsAsync(user);
                    foreach (var cloudPath in paths)
                        await GrantAccessAsync(user.Email, user.UserId, cloudPath);
                }

                var usersToRevoke = await _oneDriveRepository.GetUsersForOneDriveRemovalAsync();
                foreach (var user in usersToRevoke)
                {
                    if (token.IsCancellationRequested) break;
                    var paths = GetPathsForUser(user);
                    if (paths.Count == 0)
                    {
                        await _oneDriveRepository.MarkOneDriveAccessRevokedAsync(user.UserId);
                    }
                    else
                    {
                        foreach (var cloudPath in paths)
                            await RevokeAccessAsync(user.Email, user.UserId, cloudPath);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "КРИТИЧНА ПОМИЛКА в циклі OneDrive Permissions");
            }

            await Task.Delay(TimeSpan.FromMinutes(1), token);
        }
    }

    public void Initialize(AzureConfig config, string localOneDrivePath, string sourceRootPath)
    {
        if (config == null) throw new ArgumentNullException(nameof(config));
        if (string.IsNullOrEmpty(localOneDrivePath)) return;
        if (string.IsNullOrEmpty(config.AdminEmail)) throw new ArgumentException("AdminEmail is empty in AzureConfig");

        string normOneDrive = NormalizePath(localOneDrivePath);
        string normSource = NormalizePath(sourceRootPath);

        if (_graphClient != null && _currentConfig != null
            && _currentConfig.ClientId == config.ClientId
            && _currentConfig.ClientSecret == config.ClientSecret
            && _currentConfig.TenantId == config.TenantId
            && _localRootFullPath == normSource
            && _adminEmail == config.AdminEmail)
        {
            return;
        }

        _currentConfig = config;
        _adminEmail = config.AdminEmail;
        _localRootFullPath = normSource;
        _cloudRootFolderName = Path.GetFileName(normOneDrive);

        var credential = new ClientSecretCredential(
            config.TenantId,
            config.ClientId,
            config.ClientSecret,
            new ClientSecretCredentialOptions { AuthorityHost = AzureAuthorityHosts.AzurePublicCloud });

        _graphClient = new GraphServiceClient(credential, new[] { "https://graph.microsoft.com/.default" });
        _logger.LogInformation("OneDrivePermissonService ініціалізовано для: {Admin}", _adminEmail);
    }

    public async Task GrantAccessAsync(string userEmail, int userId, string fullLocalPath)
    {
        if (_graphClient == null)
        {
            _logger.LogError("Сервіс не ініціалізовано. Спочатку викличте Initialize().");
            return;
        }

        try
        {
            string cloudPath = ConvertLocalPathToCloudPath(fullLocalPath);
            if (string.IsNullOrEmpty(cloudPath)) return;

            var drive = await _graphClient.Users[_adminEmail].Drive.GetAsync();
            if (drive == null) return;

            var driveItem = await _graphClient.Drives[drive.Id].Root.ItemWithPath(cloudPath).GetAsync();
            if (driveItem == null)
            {
                _logger.LogError("Папка не знайдена в хмарі: {Path}", cloudPath);
                return;
            }

            var inviteBody = new InvitePostRequestBody
            {
                Recipients = new List<DriveRecipient> { new DriveRecipient { Email = userEmail } },
                Message = "Вам надано доступ до матеріалів Recon (автоматично).",
                RequireSignIn = true,
                SendInvitation = true,
                Roles = new List<string> { "read" }
            };

            await _graphClient.Drives[drive.Id].Items[driveItem.Id].Invite.PostAsync(inviteBody);
            _logger.LogInformation("Видано доступ {Email} до {Path}", userEmail, cloudPath);
            await _oneDriveRepository.MarkOneDriveAccessGrantedAsync(userId);
        }
        catch (ODataError ex)
        {
            _logger.LogError("OData помилка: {Code} - {Msg}", ex.Error?.Code, ex.Error?.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Помилка Graph API при видачі доступу для {Email}", userEmail);
        }
    }

    public async Task RevokeAccessAsync(string userEmail, int userId, string fullLocalPath)
    {
        if (_graphClient == null) return;

        try
        {
            string cloudPath = ConvertLocalPathToCloudPath(fullLocalPath);
            if (string.IsNullOrEmpty(cloudPath)) return;

            var drive = await _graphClient.Users[_adminEmail].Drive.GetAsync();
            if (drive == null) return;

            string itemId;
            try
            {
                var driveItem = await _graphClient.Drives[drive.Id].Root.ItemWithPath(cloudPath).GetAsync();
                itemId = driveItem!.Id!;
            }
            catch (ODataError ex) when (ex.Error?.Code == "itemNotFound")
            {
                _logger.LogWarning("Файл не знайдено в хмарі: {Path}. Вважаємо доступ скасованим.", cloudPath);
                await _oneDriveRepository.MarkOneDriveAccessRevokedAsync(userId);
                return;
            }

            var permissions = await _graphClient.Drives[drive.Id].Items[itemId].Permissions.GetAsync();
            if (permissions?.Value == null) return;

            var toDelete = permissions.Value.Where(p => IsMatch(p, userEmail)).ToList();
            foreach (var perm in toDelete)
            {
                try
                {
                    await _graphClient.Drives[drive.Id].Items[itemId].Permissions[perm.Id].DeleteAsync();
                    _logger.LogInformation("Видалено дозвіл {Id} для {Email}", perm.Id, userEmail);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Не вдалося видалити дозвіл {Id}", perm.Id);
                }
            }

            await _oneDriveRepository.MarkOneDriveAccessRevokedAsync(userId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Помилка при видаленні прав для {Email}", userEmail);
        }
    }

    private async Task<HashSet<string>> SyncUserPermissionsAsync(UserAccessDto user)
    {
        if (user.IsAdmin)
            return GetPathsForUser(user);

        user.IsAdmin = true;
        var paths = GetPathsForUser(user);
        foreach (var path in paths)
            await RevokeAccessAsync(user.Email, user.UserId, path);

        user.IsAdmin = false;
        return paths;
    }

    private HashSet<string> GetPathsForUser(UserAccessDto user)
    {
        var result = new HashSet<string>();
        if (user.FolderPaths == null || user.FolderPaths.Count == 0) return result;

        foreach (var localPath in user.FolderPaths)
        {
            string cloudPath = ConvertLocalPathToCloudPath(localPath);
            if (string.IsNullOrEmpty(cloudPath)) continue;

            result.Add(user.IsAdmin ? GetRootFolder(cloudPath) : cloudPath);
        }

        return result;
    }

    private bool IsMatch(Permission p, string targetEmail)
    {
        if (p.GrantedTo?.User != null && CheckIdentity(p.GrantedTo.User, targetEmail)) return true;

        if (p.GrantedToIdentities != null)
            foreach (var id in p.GrantedToIdentities)
                if (id.User != null && CheckIdentity(id.User, targetEmail)) return true;

        return false;
    }

    private static bool CheckIdentity(Identity user, string email)
    {
        if (user.Id?.Equals(email, StringComparison.OrdinalIgnoreCase) == true) return true;

        if (user.AdditionalData?.TryGetValue("email", out var emailObj) == true
            && emailObj?.ToString()?.Equals(email, StringComparison.OrdinalIgnoreCase) == true) return true;

        if (user.AdditionalData?.TryGetValue("loginName", out var loginObj) == true
            && loginObj?.ToString()?.Equals(email, StringComparison.OrdinalIgnoreCase) == true) return true;

        return false;
    }

    private string ConvertLocalPathToCloudPath(string dbFilePath)
    {
        if (string.IsNullOrEmpty(_localRootFullPath) || string.IsNullOrEmpty(dbFilePath))
            return string.Empty;

        string normDb = NormalizePath(dbFilePath);
        string normRoot = NormalizePath(_localRootFullPath).TrimEnd('\\', '/');

        if (normDb.StartsWith(normRoot, StringComparison.OrdinalIgnoreCase))
            return normDb.Substring(normRoot.Length).TrimStart('\\', '/').Replace("\\", "/");

        return normDb.TrimStart('\\', '/').Replace("\\", "/");
    }

    private static string GetRootFolder(string cloudPath)
    {
        string path = cloudPath.Replace("\\", "/").TrimStart('/');
        int slash = path.IndexOf('/');
        return slash > 0 ? path.Substring(0, slash) : path;
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        return path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                   .TrimEnd(Path.DirectorySeparatorChar);
    }
}
