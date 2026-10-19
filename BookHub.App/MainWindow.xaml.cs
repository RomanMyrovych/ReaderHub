using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BookHub.App.ViewModels;
using BookHub.App.Views;
using BookHub.Core.Models;

namespace BookHub.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private ReaderWindow? _activeReaderWindow;
    private ReaderViewModel? _activeReaderViewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;

        RestoreMainWindowAndSplittersState();

        _viewModel.OpenReaderRequested += OnOpenReaderRequested;
        _viewModel.OpenSettingsRequested += OnOpenSettingsRequested;
        _viewModel.OpenMetadataEditorRequested += OnOpenMetadataEditorRequested;
        _viewModel.OpenDuplicatesWindowRequested += OnOpenDuplicatesWindowRequested;
        _viewModel.OpenAboutWindowRequested += OnOpenAboutWindowRequested;
        _viewModel.OpenWelcomeWizardRequested += OnOpenWelcomeWizardRequested;

        Loaded += async (_, _) => await _viewModel.InitializeAsync();
        Closing += OnMainWindowClosing;
    }

    private void RestoreMainWindowAndSplittersState()
    {
        var workArea = SystemParameters.WorkArea;

        // Constrain maximized window to SystemParameters.WorkArea so Windows taskbar is never overlapped
        MaxHeight = workArea.Height + 14;
        MaxWidth = workArea.Width + 14;

        var s = _viewModel.SettingsService.Current;

        LeftSidebarColumn.Width = new GridLength(Math.Clamp(s.LeftSidebarWidth, 210, 460));
        RightInspectorColumn.Width = new GridLength(Math.Clamp(s.RightInspectorWidth, 240, 520));

        if (s.IsFirstLaunch)
        {
            s.IsFirstLaunch = false;
            Width = workArea.Width * 0.92;
            Height = workArea.Height * 0.92;
            Left = workArea.Left + (workArea.Width - Width) / 2;
            Top = workArea.Top + (workArea.Height - Height) / 2;
            WindowState = WindowState.Maximized;
            _viewModel.SettingsService.Save();
            return;
        }

        if (s.MainWindowWidth >= 800 && s.MainWindowHeight >= 550)
        {
            Width = Math.Min(s.MainWindowWidth, workArea.Width);
            Height = Math.Min(s.MainWindowHeight, workArea.Height);
        }

        // Off-screen protection (handles disconnected monitors or resolution changes)
        bool hasValidPosition = !double.IsNaN(s.MainWindowLeft) &&
                                !double.IsNaN(s.MainWindowTop) &&
                                s.MainWindowLeft >= SystemParameters.VirtualScreenLeft - 50 &&
                                s.MainWindowTop >= SystemParameters.VirtualScreenTop - 50 &&
                                s.MainWindowLeft + 200 <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth &&
                                s.MainWindowTop + 200 <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;

        if (hasValidPosition)
        {
            Left = Math.Clamp(s.MainWindowLeft, workArea.Left, Math.Max(workArea.Left, workArea.Right - Width));
            Top = Math.Clamp(s.MainWindowTop, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - Height));
        }
        else
        {
            Left = workArea.Left + (workArea.Width - Width) / 2;
            Top = workArea.Top + (workArea.Height - Height) / 2;
        }

        WindowState = s.MainWindowState == "Maximized" ? WindowState.Maximized : WindowState.Normal;
    }

    private void OnMainWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var s = _viewModel.SettingsService.Current;
        s.MainWindowState = WindowState == WindowState.Maximized ? "Maximized" : "Normal";
        if (WindowState == WindowState.Normal)
        {
            s.MainWindowWidth = Width;
            s.MainWindowHeight = Height;
            s.MainWindowLeft = Left;
            s.MainWindowTop = Top;
        }

        s.LeftSidebarWidth = LeftSidebarColumn.ActualWidth > 180 ? LeftSidebarColumn.ActualWidth : 280;
        s.RightInspectorWidth = RightInspectorColumn.ActualWidth > 200 ? RightInspectorColumn.ActualWidth : 330;

        _viewModel.SettingsService.Save();
        _viewModel.Dispose();
    }

    private async void OnOpenReaderRequested(BookRecord book)
    {
        if (_activeReaderWindow != null && _activeReaderWindow.IsLoaded && _activeReaderViewModel != null)
        {
            await _activeReaderViewModel.OpenBookInNewTabAsync(book);
            _activeReaderWindow.Activate();
            return;
        }

        _activeReaderViewModel = new ReaderViewModel(book, _viewModel.Database, _viewModel.SettingsService);
        _activeReaderWindow = new ReaderWindow(_activeReaderViewModel, _viewModel.SettingsService, () =>
        {
            var readBook = _activeReaderViewModel?.ActiveTab?.Book ?? book;
            _activeReaderWindow = null;
            _activeReaderViewModel = null;
            _viewModel.UpdateBookProgressFromReader(readBook);
            return Task.CompletedTask;
        })
        {
            Owner = this
        };
        _activeReaderWindow.Show();
    }

    private void OnOpenSettingsRequested()
    {
        var settingsVm = new SettingsViewModel(
            _viewModel.Database,
            _viewModel.Scanner,
            _viewModel.SettingsService,
            async () => await _viewModel.RunFullScanAsync());

        var settingsWin = new SettingsWindow(settingsVm)
        {
            Owner = this
        };
        settingsWin.ShowDialog();
    }

    private void OnOpenAboutWindowRequested()
    {
        var aboutWin = new AboutWindow
        {
            Owner = this
        };
        aboutWin.ShowDialog();
    }

    private async void OnOpenWelcomeWizardRequested()
    {
        var wizardVm = new WelcomeWizardViewModel(_viewModel.Database, _viewModel.SettingsService);
        var wizardWin = new WelcomeWizardWindow(wizardVm)
        {
            Owner = this
        };
        if (wizardWin.ShowDialog() == true)
        {
            _viewModel.ReloadSettingsFromWizard();
            await _viewModel.Scanner.RefreshWatchersAsync();
            await _viewModel.RunFullScanAsync();
        }
    }

    private async void OnOpenMetadataEditorRequested(List<BookRecord> books)
    {
        if (books.Count == 0) return;
        var vm = new MetadataEditorViewModel(_viewModel.Database, books);
        var win = new MetadataEditorWindow(vm)
        {
            Owner = this
        };
        if (win.ShowDialog() == true)
        {
            await _viewModel.ReloadLibraryFromDatabaseAsync();
        }
    }

    private void OnOpenDuplicatesWindowRequested()
    {
        var win = new DuplicatesWindow(_viewModel.Database, async () =>
        {
            await _viewModel.ReloadLibraryFromDatabaseAsync();
        })
        {
            Owner = this
        };
        win.ShowDialog();
    }

    private void BatchEditButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = new List<BookRecord>();
        if (_viewModel.IsTableViewMode)
        {
            foreach (var item in BooksDataGrid.SelectedItems)
            {
                if (item is BookRecord b) selected.Add(b);
            }
        }
        else
        {
            foreach (var item in BooksListBox.SelectedItems)
            {
                if (item is BookRecord b) selected.Add(b);
            }
        }

        if (selected.Count == 0 && _viewModel.SelectedBook != null)
        {
            selected.Add(_viewModel.SelectedBook);
        }

        if (selected.Count == 0)
        {
            MessageBox.Show("Виділіть одну або кілька книг (за допомогою Ctrl або Shift) для редагування метаданих.",
                "Редактор метаданих", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _viewModel.RequestBatchMetadataEditor(selected);
    }

    private void SmartCollectionsListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox listBox)
        {
            DeselectIfAlreadySelected(listBox, e, () => _viewModel.SelectedSmartCollection = null);
        }
    }

    private void ShelvesListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox listBox)
        {
            DeselectIfAlreadySelected(listBox, e, () => _viewModel.SelectedShelf = null);
        }
    }

    private static void DeselectIfAlreadySelected(ListBox listBox, MouseButtonEventArgs e, Action clearSelection)
    {
        if (e.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement(listBox, source) is not ListBoxItem container ||
            !ReferenceEquals(container.Content, listBox.SelectedItem))
        {
            return;
        }

        e.Handled = true;
        clearSelection();
    }

    private async void AssignShelfCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox combo && combo.SelectedItem is VirtualShelfRecord shelf && _viewModel.SelectedBook != null)
        {
            var book = _viewModel.SelectedBook;
            combo.SelectionChanged -= AssignShelfCombo_SelectionChanged;
            try
            {
                await _viewModel.AddSelectedBookToShelfCommand.ExecuteAsync(shelf);
                combo.SelectedItem = null;
            }
            finally
            {
                combo.SelectionChanged += AssignShelfCombo_SelectionChanged;
            }
            _viewModel.SelectedBook = book;
        }
    }

    private void BooksListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.SelectedBook != null)
        {
            OnOpenReaderRequested(_viewModel.SelectedBook);
        }
    }
}