using Microsoft.Win32;
using System.Security.Cryptography;
using System.Text;

namespace Recon.UI;

internal sealed record SavedCredentials(
    string Login, string Password, string ConfigFile, bool IsSaveParams);

internal static class CredentialStore
{
    private const string RegPath = @"Software\ReconC#\Integration";

    public static SavedCredentials Load()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegPath);
        if (key == null) return new SavedCredentials("", "", "", false);

        string login      = key.GetValue("LastUsedLogin")?.ToString()  ?? "";
        bool   isSave     = key.GetValue("IsSaveParams")?.ToString()   == "True";
        string configFile = key.GetValue("LastUsedConfig")?.ToString() ?? "";
        string password   = DecryptPassword(key.GetValue("LastUsedPassword")?.ToString());

        return new SavedCredentials(login, password, configFile, isSave);
    }

    // configFile is always saved (not sensitive).
    // login/password only when isSaveParams = true; cleared otherwise.
    public static void Save(string login, string password, bool isSaveParams, string configFile)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegPath);
        if (key == null) return;

        key.SetValue("LastUsedConfig",  configFile);
        key.SetValue("IsSaveParams",    isSaveParams);

        if (isSaveParams)
        {
            key.SetValue("LastUsedLogin",    login);
            key.SetValue("LastUsedPassword", EncryptPassword(password));
        }
        else
        {
            key.DeleteValue("LastUsedLogin",    throwOnMissingValue: false);
            key.DeleteValue("LastUsedPassword", throwOnMissingValue: false);
        }
    }

    private static string EncryptPassword(string password)
    {
        if (string.IsNullOrEmpty(password)) return "";
        byte[] enc = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(password), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(enc);
    }

    private static string DecryptPassword(string? encrypted)
    {
        if (string.IsNullOrEmpty(encrypted)) return "";
        try
        {
            byte[] dec = ProtectedData.Unprotect(
                Convert.FromBase64String(encrypted), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(dec);
        }
        catch { return ""; }
    }
}
