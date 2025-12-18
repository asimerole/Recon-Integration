using System.Windows;

namespace Recon.UI;

public partial class InputWindow : Window
{
    public string ResponseText { get; private set; } = string.Empty;

    public InputWindow(string prompt, string title = "Повідомлення")
    {
        InitializeComponent();
        
        PromptTextBlock.Text = prompt;
        this.Title = title;
        
        ResponseTextBox.Focus(); 
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        ResponseText = ResponseTextBox.Text;
        DialogResult = true; 
    }
}