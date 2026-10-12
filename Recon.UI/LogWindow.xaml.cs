using System.Windows;
using Recon.UI.ViewModels;

namespace Recon.UI;

public partial class LogWindow : Window
{
    public LogWindow(LogViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Closing += (_, _) => vm.StopTimer();
    }

    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
}
