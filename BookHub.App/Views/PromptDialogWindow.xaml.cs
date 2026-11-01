using System.Windows;
using System.Windows.Input;

namespace BookHub.App.Views;

public partial class PromptDialogWindow : Window
{
    public string? ResponseText { get; private set; }

    public PromptDialogWindow(string title, string prompt, string defaultValue = "")
    {
        InitializeComponent();
        TitleTextBlock.Text = title;
        PromptTextBlock.Text = prompt;
        InputTextBox.Text = defaultValue;
        InputTextBox.Focus();
        InputTextBox.SelectAll();
    }

    public static string? Show(Window? owner, string title, string prompt, string defaultValue = "")
    {
        var dlg = new PromptDialogWindow(title, prompt, defaultValue);
        if (owner != null && owner.IsVisible)
        {
            dlg.Owner = owner;
        }
        else if (Application.Current.MainWindow != null && Application.Current.MainWindow.IsVisible)
        {
            dlg.Owner = Application.Current.MainWindow;
        }

        bool? result = dlg.ShowDialog();
        return result == true ? dlg.ResponseText : null;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        ResponseText = InputTextBox.Text.Trim();
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        ResponseText = null;
        DialogResult = false;
        Close();
    }

    private void InputTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OkButton_Click(sender, e);
        }
        else if (e.Key == Key.Escape)
        {
            CancelButton_Click(sender, e);
        }
    }
}
