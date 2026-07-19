using System.Windows;
using System.IO;
using Microsoft.Win32;
using System.Security.Cryptography;
using System.Text;
using Recon.Core.Dtos;
using Recon.Core.Interfaces;
using MessageBox = System.Windows.Forms.MessageBox;

namespace Recon.UI;

public partial class AuthWindow : Window
{
    private readonly IAuthService _authService;
    private readonly IConfigService _configService;
    public bool IsAuthenticated { get; set; } = false;

    public AuthWindow(IAuthService authService, IConfigService configService)
    {
        InitializeComponent();
        _authService = authService;
        _configService = configService;

        var creds = LoadParamsFromRegistry();
        LoginBox.Text = creds.Login;
        HiddenPasswordBox.Password = creds.Password;
        SaveParamsToRegistryToggle.IsChecked = creds.IsSaveParams;
        LoadConfigFiles();
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        var basePath = AppDomain.CurrentDomain.BaseDirectory;
        var path = basePath + ConfigComboBox.SelectedItem;

        string login = LoginBox.Text;
        string password = HiddenPasswordBox.IsVisible
            ? HiddenPasswordBox.Password
            : VisiblePasswordBox.Text;

        if (!string.IsNullOrWhiteSpace(login) && !string.IsNullOrWhiteSpace(password))
        {
            var dbOptions = _configService.LoadDatabaseConfig(path);
            bool success = await _authService.LoginAsync(login, password, dbOptions);

            if (success)
            {
                var isSave = SaveParamsToRegistryToggle.IsChecked;
                if (isSave == true)
                    SaveParamsToRegistry(login, password, isSave);

                IsAuthenticated = true;
                DialogResult = true;
                Close();
            }
        }
        else
        {
            MessageBox.Show("Логін або пароль порожні");
        }
    }

    private void LoadConfigFiles()
    {
        try
        {
            var appDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string[] configFiles = Directory.GetFiles(appDirectory, "*.recon");

            foreach (var filePath in configFiles)
                ConfigComboBox.Items.Add(Path.GetFileName(filePath));

            if (ConfigComboBox.Items.Count > 0)
                ConfigComboBox.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Помилка при пошуку конфігів: {ex.Message}");
        }
    }

    private void RevealPassword_Checked(object sender, RoutedEventArgs e)
    {
        VisiblePasswordBox.Text = HiddenPasswordBox.Password;
        HiddenPasswordBox.Visibility = Visibility.Collapsed;
        VisiblePasswordBox.Visibility = Visibility.Visible;
    }

    private void RevealPassword_Unchecked(object sender, RoutedEventArgs e)
    {
        HiddenPasswordBox.Password = VisiblePasswordBox.Text;
        VisiblePasswordBox.Visibility = Visibility.Collapsed;
        HiddenPasswordBox.Visibility = Visibility.Visible;
    }

    private void Border_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
            DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    void SaveParamsToRegistry(string username, string password, bool? isSave)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\ReconC#\Integration");
        if (key != null)
        {
            key.SetValue("LastUsedLogin", username);
            key.SetValue("IsSaveParams", isSave);
            if (!string.IsNullOrEmpty(password))
            {
                byte[] encrypted = ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(password), null, DataProtectionScope.CurrentUser);
                key.SetValue("LastUsedPassword", Convert.ToBase64String(encrypted));
            }
            else
            {
                key.DeleteValue("LastUsedPassword", throwOnMissingValue: false);
            }
        }
    }

    public (string Login, string Password, bool IsSaveParams) LoadParamsFromRegistry()
    {
        string login = "", password = "";
        bool isSaveChecked = false;

        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\ReconC#\Integration");
        if (key != null)
        {
            login = key.GetValue("LastUsedLogin")?.ToString() ?? "";
            isSaveChecked = key.GetValue("IsSaveParams")?.ToString() == "True";
            string encryptedPass = key.GetValue("LastUsedPassword")?.ToString() ?? "";
            if (!string.IsNullOrEmpty(encryptedPass))
            {
                byte[] decrypted = ProtectedData.Unprotect(
                    Convert.FromBase64String(encryptedPass), null, DataProtectionScope.CurrentUser);
                password = Encoding.UTF8.GetString(decrypted);
            }
        }

        return (login, password, isSaveChecked);
    }
}
