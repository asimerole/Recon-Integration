using Azure.Identity;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Drives.Item.Items.Item.Invite;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Extensions.Logging;
using Recon.Core.Interfaces.Repositories;
using Recon.Core.Models;
using Recon.Core.Options;
using System.IO;

namespace Recon.Core.Services;

public class OneDrivePermissonService
{
    private GraphServiceClient _graphClient;
    
    private string _cloudRootFolderName; 
    private string _localRootFullPath;
    private string _adminEmail;
    
    // Зберігаємо поточний конфіг, щоб не перестворювати клієнта дарма
    private AzureConfig _currentConfig;
    
    private Task _workingTask;
    private CancellationTokenSource _cts;
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

    private string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;

        return path
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar) // Міняємо / на \
            .TrimEnd(Path.DirectorySeparatorChar); // Прибираємо хвіст
    }
    
    public void Initialize(AzureConfig config, string localOneDrivePath, string sourceRootPath)
    {
        // Валідація вхідних даних
        if (config == null) throw new ArgumentNullException(nameof(config));
        if (string.IsNullOrEmpty(localOneDrivePath)) return;
        if (string.IsNullOrEmpty(config.AdminEmail)) throw new ArgumentException("AdminEmail is empty in AzureConfig");

        string normOneDrive = NormalizePath(localOneDrivePath);
        string normSource = NormalizePath(sourceRootPath);
        
        // Економія ресурсів: якщо налаштування ті ж самі - виходимо
        if (_graphClient != null && 
            _currentConfig != null &&
            _currentConfig.ClientId == config.ClientId &&
            _currentConfig.ClientSecret == config.ClientSecret &&
            _currentConfig.TenantId == config.TenantId &&
            _localRootFullPath == localOneDrivePath &&
            _adminEmail == config.AdminEmail)
        {
            return;
        }

        // Оновлюємо локальні поля
        _currentConfig = config;
        _adminEmail = config.AdminEmail;
        
        _localRootFullPath = normSource;
        _cloudRootFolderName = Path.GetFileName(normOneDrive);   
        
        // Створюємо клієнта
        var options = new ClientSecretCredentialOptions
        {
            AuthorityHost = AzureAuthorityHosts.AzurePublicCloud
        };

        var clientSecretCredential = new ClientSecretCredential(
            config.TenantId, 
            config.ClientId, 
            config.ClientSecret, 
            options);
            
        _graphClient = new GraphServiceClient(clientSecretCredential, new[] { "https://graph.microsoft.com/.default" });
        
        _logger.LogInformation($"[OneDriveService] Ініціалізовано для адміна: {_adminEmail}");
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
        // Генеруємо ID для цього проходу циклу, щоб легше читати логи
        string cycleId = Guid.NewGuid().ToString().Substring(0, 5).ToUpper();
        _logger.LogInformation($"[{cycleId}] === ПОЧАТОК ЦИКЛУ ONEDRIVE SYNC ===");

        try
        {
            // 1. Читаємо конфіг
            var azureConfig = await _configRepository.GetAzureConfigAsync();
            var oneDrivePath = azureConfig!.OneDrivePath;
            var sourceRootPath = await _configRepository.GetRootFolderAsync();
            
            // 2. Ініціалізація
            Initialize(azureConfig, oneDrivePath, sourceRootPath);
            
            // ============================================
            // БЛОК 1: ВИДАЧА ПРАВ (GRANT)
            // ============================================
            var usersToGrant = await _oneDriveRepository.GetUsersForOneDriveUpdateAsync();
            
            if (usersToGrant.Count > 0)
            {
                _logger.LogInformation($"[{cycleId}] [GRANT] Знайдено користувачів для видачі прав: {usersToGrant.Count}");
            }
            else
            {
                _logger.LogInformation($"[{cycleId}] [GRANT] Немає нових користувачів для обробки.");
            }

            foreach (var user in usersToGrant)
            {
                if (token.IsCancellationRequested) break;
                
                // Отримуємо відфільтровані шляхи (вже з урахуванням Admin/User)
                var pathsToProcess = await SyncUserPermissionsAsync(user);
                
                _logger.LogInformation($"[{cycleId}] [GRANT] Обробка User: {user.Email} (ID: {user.UserId}). " +
                                       $"Роль: {(user.IsAdmin ? "ADMIN" : "USER")}. " +
                                       $"Знайдено унікальних шляхів: {pathsToProcess.Count}");

                foreach (var cloudPath in pathsToProcess)
                {
                    _logger.LogInformation($"[{cycleId}]    -> Видаємо доступ до: '{cloudPath}'");
                    await GrantAccessAsync(user.Email, user.UserId, cloudPath);
                }
            }

            // ============================================
            // БЛОК 2: ЗАБИРАННЯ ПРАВ (REVOKE)
            // ============================================
            var usersToRevoke = await _oneDriveRepository.GetUsersForOneDriveRemovalAsync();
            
            if (usersToRevoke.Count > 0)
            {
                _logger.LogInformation($"[{cycleId}] [REVOKE] Знайдено користувачів для видалення прав: {usersToRevoke.Count}");
            }

            foreach (var user in usersToRevoke)
            {
                if (token.IsCancellationRequested) break;

                var pathsToProcess = GetPathsForUser(user);
                
                _logger.LogInformation($"[{cycleId}] [REVOKE] Обробка User: {user.Email}. " +
                                       $"Роль: {(user.IsAdmin ? "ADMIN" : "USER")}. " +
                                       $"Шляхів на видалення: {pathsToProcess.Count}");
                
                if (pathsToProcess.Count == 0)
                {
                    await _oneDriveRepository.MarkOneDriveAccessRevokedAsync(user.UserId);
                }

                foreach (var cloudPath in pathsToProcess)
                {
                    _logger.LogInformation($"[{cycleId}]    -> Забираємо доступ у: '{cloudPath}'");
                    await RevokeAccessAsync(user.Email, user.UserId, cloudPath);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"[{cycleId}] КРИТИЧНА ПОМИЛКА в циклі OneDrive Permissions");
        }

        _logger.LogInformation($"[{cycleId}] === ЦИКЛ ЗАВЕРШЕНО. Очікування 1 хв... ===");
        
        await Task.Delay(TimeSpan.FromMinutes(1), token);
    }
    }
    private async Task<HashSet<string>> SyncUserPermissionsAsync(UserAccessDto user)
    {
        if (user.IsAdmin )
        {
            return GetPathsForUser(user);
            
        }
        else
        {
            user.IsAdmin = true;
            var paths = GetPathsForUser(user);
        
            foreach (var path in paths)
            {
                await RevokeAccessAsync(user.Email, user.UserId, path);    
            }
        
            user.IsAdmin = false;
            //paths = GetPathsForUser(user); // for test. but in release should be uncomm..
            return paths;
        }
       return new HashSet<string>();
    }
    
    public async Task GrantAccessAsync(string userEmail, int userId, string fullLocalPath)
    {
        // Захист від виклику до ініціалізації
        if (_graphClient == null)
        {
            _logger.LogError("[OneDriveService] Помилка: Сервіс не ініціалізовано. Спочатку викличте Initialize().");
            return;
        }

        try
        {
            string cloudPath = ConvertLocalPathToCloudPath(fullLocalPath);
            if (string.IsNullOrEmpty(cloudPath))
            {
                _logger.LogError($"[OneDriveService] Шлях не належить кореневій папці: {fullLocalPath}");
                return;
            }
            
            // Використовуємо збережений _adminEmail
            var drive = await _graphClient.Users[_adminEmail].Drive.GetAsync();
            
            if (drive == null)
            {
                _logger.LogError($"[OneDriveService] Не вдалося знайти диск OneDrive для користувача {_adminEmail}");
                return;
            }

            string driveId = drive.Id;
            
            // Шукаємо папку
            var driveItem = await _graphClient.Drives[driveId]
                .Root
                .ItemWithPath(cloudPath) 
                .GetAsync();

            if (driveItem == null)
            {
                _logger.LogError($"[OneDriveService] Папка не знайдена в хмарі: {cloudPath}");
                return;
            }

            string itemId = driveItem.Id;

            // Формуємо запрошення
            var inviteBody = new InvitePostRequestBody
            {
                Recipients = new List<DriveRecipient>
                {
                    new DriveRecipient { Email = userEmail }
                },
                Message = "Вам надано доступ до матеріалів Recon (автоматично).",
                RequireSignIn = true,
                SendInvitation = true,
                Roles = new List<string> { "read" } 
            };

            await _graphClient.Drives[driveId]
                .Items[itemId]
                .Invite
                .PostAsync(inviteBody);

            _logger.LogInformation($"[OneDriveService] Успішно видано доступ {userEmail} до {cloudPath}");
            
            // Помічаємо в базі, що права видано (щоб не слати запити вічно)
            await _oneDriveRepository.MarkOneDriveAccessGrantedAsync(userId);
        }
        catch (ODataError odataEx)
        {
            _logger.LogError($"[OneDriveService] Помилка OData: {odataEx.Error?.Code} - {odataEx.Error?.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError($"[OneDriveService] Помилка Graph API: {ex.Message}");
        }
    }
    
    public async Task RevokeAccessAsync(string userEmail, int userId, string fullLocalPath)
    {
        if (_graphClient == null) return;
    
        try
        {
            string cloudPath = ConvertLocalPathToCloudPath(fullLocalPath);
            if (string.IsNullOrEmpty(cloudPath)) return;
    
            // 1. Отримуємо ID диска та файлу
            var drive = await _graphClient.Users[_adminEmail].Drive.GetAsync();
            string driveId = drive.Id;
            string itemId;
            try 
            {
                // 2. Спробуємо знайти файл
                var driveItem = await _graphClient.Drives[driveId]
                    .Root
                    .ItemWithPath(cloudPath) 
                    .GetAsync();
            
                itemId = driveItem.Id;
            }
            catch (ODataError ex) when (ex.Error?.Code == "itemNotFound")
            {
                _logger.LogWarning($"[Revoke] Файл не знайдено в хмарі: {cloudPath}. Вважаємо доступ скасованим.");
                
                await _oneDriveRepository.MarkOneDriveAccessRevokedAsync(userId);
                return; 
            }
    
            var permissions = await _graphClient.Drives[driveId]
                .Items[itemId]
                .Permissions
                .GetAsync();

            if (permissions?.Value != null)
            {
                var permsToDelete = permissions.Value
                    .Where(p => IsMatch(p, userEmail))
                    .ToList();

                if (permsToDelete.Count > 0)
                {
                    _logger.LogInformation($"[Revoke] Знайдено {permsToDelete.Count} активних прав для {userEmail}. Починаємо видалення...");

                    foreach (var perm in permsToDelete)
                    {
                        try
                        {
                            _logger.LogInformation($"[Revoke] Спроба видалити PermissionId: {perm.Id}...");

                            await _graphClient.Drives[driveId]
                                .Items[itemId]
                                .Permissions[perm.Id]
                                .DeleteAsync();

                            _logger.LogInformation($"[Revoke] [SUCCESS] PermissionId {perm.Id} успішно видалено (API 204).");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"[Revoke] [ERROR] Не вдалося видалити PermissionId {perm.Id}");
                        }
                    }
                }
                else
                {
                    _logger.LogWarning($"[Revoke] Права для {userEmail} на файлі {cloudPath} не знайдені (список пустий).");
                }
            }
    
            // 5. Оновлюємо статус у БД
            await _oneDriveRepository.MarkOneDriveAccessRevokedAsync(userId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Помилка при видаленні прав для {userEmail}");
        }
    }

    // === ДОДАТКОВИЙ МЕТОД ДЛЯ ПОРІВНЯННЯ ===
    private bool IsMatch(Permission p, string targetEmail)
    {
        if (p.GrantedTo?.User != null)
        {
            if (CheckIdentity(p.GrantedTo.User, targetEmail)) return true;
        }
    
        // Перевірка для колекцій (якщо доступ надано через спільне посилання декільком)
        if (p.GrantedToIdentities != null)
        {
            foreach (var identitySet in p.GrantedToIdentities)
            {
                if (identitySet.User != null && CheckIdentity(identitySet.User, targetEmail))
                    return true;
            }
        }
    
        return false;
    }
    
    private bool CheckIdentity(Identity user, string email)
    {
        // 1. Іноді Id співпадає з email (для зовнішніх користувачів)
        if (user.Id != null && user.Id.Equals(email, StringComparison.OrdinalIgnoreCase)) 
            return true;
    
        // 2. У SDK v5 email часто ховається в AdditionalData
        if (user.AdditionalData != null && user.AdditionalData.TryGetValue("email", out var emailObj))
        {
            if (emailObj?.ToString()?.Equals(email, StringComparison.OrdinalIgnoreCase) == true)
                return true;
        }
        
        // 3. Також перевіряємо loginName (іноді буває там)
        if (user.AdditionalData != null && user.AdditionalData.TryGetValue("loginName", out var loginObj))
        {
            if (loginObj?.ToString()?.Equals(email, StringComparison.OrdinalIgnoreCase) == true)
                return true;
        }
        
        return false;
    }

    private string ConvertLocalPathToCloudPath(string dbFilePath)
    {
        string rootPath = _localRootFullPath; 
        
        if (string.IsNullOrEmpty(rootPath))
        {
            _logger.LogError($"[PathError] _localRootFullPath пустий! Перевірте Initialize(). dbFilePath: {dbFilePath}");
            return null;
        }
    
        if (string.IsNullOrEmpty(dbFilePath)) 
        {
            _logger.LogWarning($"[PathError] Прийшов пустий шлях з бази даних.");
            return null;
        }

        string normDbPath = NormalizePath(dbFilePath);  
        string normRootPath = NormalizePath(rootPath);   

        normRootPath = normRootPath.TrimEnd('\\', '/');

        if (normDbPath.StartsWith(normRootPath, StringComparison.OrdinalIgnoreCase))
        {
            string relative = normDbPath.Substring(normRootPath.Length);
            var result = relative.TrimStart('\\', '/').Replace("\\", "/");
            _logger.LogInformation($"[PathDebug] Match! Result: {result}");
            return result;
        }
        
        //_logger.LogWarning($"[PathMismatch] Шлях не починається з кореня. Root: '{normRootPath}', Target: '{normDbPath}'. Використовуємо як відносний.");
    
        var fallbackResult = normDbPath.TrimStart('\\', '/').Replace("\\", "/");
        return fallbackResult;
    }
    
    private HashSet<string> GetPathsForUser(UserAccessDto user)
    {
        var uniquePaths = new HashSet<string>();
        
        if (user.FolderPaths == null || user.FolderPaths.Count == 0)
        {
            _logger.LogWarning($"[GetPaths] У користувача {user.Email} список FolderPaths пустий або null!");
            return uniquePaths;
        }

        foreach (var localPath in user.FolderPaths)
        {
            string cloudPath = ConvertLocalPathToCloudPath(localPath);
            
            if (string.IsNullOrEmpty(cloudPath)) 
            {
                _logger.LogWarning($"[GetPaths] Шлях відкинуто (null/empty) після конвертації: '{localPath}'");
                continue;
            }

            if (user.IsAdmin)
            {
                string rootFolder = GetRootFolder(cloudPath);
        
                if (!string.IsNullOrEmpty(rootFolder))
                {
                    uniquePaths.Add(rootFolder);
                }
                else 
                {
                    _logger.LogWarning($"[GetPaths] Не вдалося визначити RootFolder для: '{cloudPath}'");
                }
            }
            else
            {
                uniquePaths.Add(cloudPath);
            }
        }
        
        _logger.LogInformation($"[GetPaths] User: {user.Email}. Raw: {user.FolderPaths.Count} -> Unique: {uniquePaths.Count}");

        return uniquePaths;
    }
    
    private string GetRootFolder(string cloudPath)
    {
        // Нормализуем слеши, чтобы не было путаницы
        string path = cloudPath.Replace("\\", "/").TrimStart('/');
    
        int firstSlash = path.IndexOf('/');
    
        if (firstSlash > 0)
        {
            // Возвращаем подстроку от начала до первого слеша
            return path.Substring(0, firstSlash);
        }
    
        // Если слешей нет (например, путь просто "ОСП"), возвращаем как есть
        return path;
    }
}