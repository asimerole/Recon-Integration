using System.Windows;
using System.IO;
using System.Windows.Input;
using Recon.Core.Interfaces;
using System.Windows.Forms;
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

        var creds = CredentialStore.Load();
        LoginBox.Text = creds.Login;
        HiddenPasswordBox.Password = creds.Password;
        SaveParamsToRegistryToggle.IsChecked = creds.IsSaveParams;
        LoadConfigFiles(creds.ConfigFile);
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        var basePath = AppDomain.CurrentDomain.BaseDirectory;
        var configFile = ConfigComboBox.SelectedItem?.ToString() ?? "";
        var path = Path.Combine(basePath, configFile);

        string login = LoginBox.Text;
        string password = HiddenPasswordBox.IsVisible
            ? HiddenPasswordBox.Password
            : VisiblePasswordBox.Text;

        if (string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(password))
        {
            MessageBox.Show("Логін або пароль порожні");
            return;
        }

        var dbOptions = _configService.LoadDatabaseConfig(path);
        bool success = await _authService.LoginAsync(login, password, dbOptions);

        if (success)
        {
            CredentialStore.Save(login, password, SaveParamsToRegistryToggle.IsChecked == true, configFile);
            IsAuthenticated = true;
            DialogResult = true;
            Close();
        }
        else
        {
            MessageBox.Show("Невірний логін або пароль.", "Помилка входу",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void LoadConfigFiles(string lastUsedConfig = "")
    {
        try
        {
            var appDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string[] configFiles = Directory.GetFiles(appDirectory, "*.recon");

            foreach (var filePath in configFiles)
                ConfigComboBox.Items.Add(Path.GetFileName(filePath));

            if (ConfigComboBox.Items.Count == 0) return;

            // Select the last used config if it exists; otherwise fall back to the first item
            int lastIndex = ConfigComboBox.Items.IndexOf(lastUsedConfig);
            ConfigComboBox.SelectedIndex = lastIndex >= 0 ? lastIndex : 0;
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

    private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
