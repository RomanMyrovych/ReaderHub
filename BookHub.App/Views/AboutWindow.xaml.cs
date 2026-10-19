using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using BookHub.Core.Data;

namespace BookHub.App.Views;

public partial class AboutWindow : Window
{
    private readonly string _licensePath;
    private readonly string _thirdPartyPath;

    public AboutWindow()
    {
        InitializeComponent();
        _licensePath = LibraryDatabase.ResolveResourceFile("LICENSE");
        _thirdPartyPath = LibraryDatabase.ResolveResourceFile("THIRD_PARTY_LICENSES.txt");

        LoadLicensesContent();
    }

    private void LoadLicensesContent()
    {
        var sb = new StringBuilder();
        if (File.Exists(_licensePath))
        {
            sb.AppendLine("=== LICENSE (MIT) ===");
            sb.AppendLine(File.ReadAllText(_licensePath, Encoding.UTF8));
            sb.AppendLine();
        }

        if (File.Exists(_thirdPartyPath))
        {
            sb.AppendLine(File.ReadAllText(_thirdPartyPath, Encoding.UTF8));
        }

        LicensesTextBox.Text = sb.ToString();
    }

    private void OpenLicenseButton_Click(object sender, RoutedEventArgs e)
    {
        if (File.Exists(_licensePath))
        {
            Process.Start(new ProcessStartInfo { FileName = "notepad.exe", Arguments = $"\"{_licensePath}\"", UseShellExecute = true });
        }
    }

    private void OpenThirdPartyButton_Click(object sender, RoutedEventArgs e)
    {
        if (File.Exists(_thirdPartyPath))
        {
            Process.Start(new ProcessStartInfo { FileName = "notepad.exe", Arguments = $"\"{_thirdPartyPath}\"", UseShellExecute = true });
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
