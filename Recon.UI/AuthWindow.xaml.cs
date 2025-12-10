using System.Windows;
using System.Windows.Controls;
using System.IO;
using Microsoft.Win32;
using System.Security.Cryptography;
using System.Text;
using Recon.Core.Interfaces;
using MessageBox = System.Windows.Forms.MessageBox;

namespace Recon.UI;

/// <summary>
/// Interaction logic for AuthWindow.xaml
/// </summary>
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
        LoadConfigFiles();
    }
    
    private void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        var basePath = AppDomain.CurrentDomain.BaseDirectory;
        var path = basePath + ConfigComboBox.SelectedItem;
        
        string login = LoginBox.Text;
        string password;
        if (ShowPasswordCheck.IsChecked == true)
        {
            password = VisiblePasswordBox.Text;
        }
        else
        {
            password = HiddenPasswordBox.Password;
        }
        
        if (!string.IsNullOrWhiteSpace(login) && !string.IsNullOrWhiteSpace(password))
        {
            var dbOptions = _configService.LoadDatabaseConfig(path);
            
            bool success = _authService.Login(login, password, dbOptions);

            if (success)
            {
                if (SaveParamsToRegistryCheck.IsChecked == true)
                {
                    SaveParamsToRegistry(login, password);
                }
                IsAuthenticated = true;
                this.DialogResult = true;
                this.Close();
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
            string[] configFiles = Directory.GetFiles(appDirectory, "*.conf");
            
            foreach (var filePath in configFiles)
            {
                string fileName = Path.GetFileName(filePath);

                ConfigComboBox.Items.Add(fileName);
            }
            
            if (ConfigComboBox.Items.Count > 0)
            {
                ConfigComboBox.SelectedIndex = 0;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Помилка при пошуку конфігів: {ex.Message}");
        }
    }
    private void ShowPasswordCheck_OnClick(object sender, RoutedEventArgs e)
    {
        var checkBox = sender as CheckBox;
        if (checkBox!.IsChecked == true)
        {
            VisiblePasswordBox.Text = HiddenPasswordBox.Password;
            
            HiddenPasswordBox.Visibility = Visibility.Collapsed;
            VisiblePasswordBox.Visibility = Visibility.Visible;
        }
        else
        {
            HiddenPasswordBox.Password = VisiblePasswordBox.Text;
            
            VisiblePasswordBox.Visibility = Visibility.Collapsed;
            HiddenPasswordBox.Visibility = Visibility.Visible;
            
            HiddenPasswordBox.Focus();
        }
    }

    void SaveParamsToRegistry(string username, string password)
    {
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\ReconC#\Integration"))
        {
            if (key != null)
            {
                key.SetValue("LastUsedLogin", username);


                if (!string.IsNullOrEmpty(password))
                {
                    byte[] data = Encoding.UTF8.GetBytes(password);
                    
                    byte[] encrypted = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
                    
                    key.SetValue("LastUsedPassword", Convert.ToBase64String(encrypted));
                }
            }
        }
        
    }
    
    public (string Login, string Password) LoadParamsFromRegistry()
    {
        string login = "";
        string password = "";
        
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\ReconC#\Integration"))
        {
            if (key != null)
            {
                login = key.GetValue("LastUsedLogin")?.ToString() ?? "";
                
                string encryptedPass = key.GetValue("LastUsedPassword")?.ToString() ?? "";
                
                if (!string.IsNullOrEmpty(encryptedPass))
                {

                    byte[] encryptedBytes = Convert.FromBase64String(encryptedPass);
                    byte[] decryptedBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
                    password = Encoding.UTF8.GetString(decryptedBytes);
                }
            }
        }
    
        return (login, password);
    }
}