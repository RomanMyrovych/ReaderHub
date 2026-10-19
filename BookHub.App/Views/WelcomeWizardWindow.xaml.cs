using System.IO;
using System.Windows;
using System.Windows.Input;
using BookHub.App.ViewModels;

namespace BookHub.App.Views;

public partial class WelcomeWizardWindow : Window
{
    private readonly WelcomeWizardViewModel _viewModel;

    public WelcomeWizardWindow(WelcomeWizardViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;

        _viewModel.WizardFinished += OnWizardFinished;
        Loaded += async (_, _) => await _viewModel.LoadInitialFoldersAsync();
    }

    private void OnWizardFinished(bool completedFullWizard)
    {
        DialogResult = true;
        Close();
    }

    private void HeaderBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void FoldersDropZone_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void FoldersDropZone_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] droppedPaths) return;

        foreach (var path in droppedPaths)
        {
            if (Directory.Exists(path))
            {
                _viewModel.AddFolderPathIfValid(path);
            }
            else if (File.Exists(path))
            {
                string? parentDir = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(parentDir))
                {
                    _viewModel.AddFolderPathIfValid(parentDir);
                }
            }
        }
    }
}
