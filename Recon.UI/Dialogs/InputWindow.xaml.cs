using System.Windows;
using System.Windows.Input;

namespace Recon.UI;

public partial class InputWindow : Window
{
    public string ResponseText { get; private set; } = string.Empty;

    public InputWindow(string prompt, string title = "Повідомлення")
    {
        InitializeComponent();

        PromptTextBlock.Text = prompt;
        TitleTextBlock.Text = title;

        ResponseTextBox.Focus();
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        ResponseText = ResponseTextBox.Text;
        DialogResult = true;
    }

    private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }
}