using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using BookHub.App.ViewModels;
using BookHub.App.Views;
using BookHub.Core.Data;
using BookHub.Core.Models;

namespace BookHub.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private ReaderWindow? _activeReaderWindow;
    private ReaderViewModel? _activeReaderViewModel;
    private readonly HashSet<long> _booksOpenedInExternalReader = [];

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;

        RestoreMainWindowAndSplittersState();
        RestoreTableColumnsState();

        _viewModel.OpenReaderRequested += OnOpenReaderRequested;
        _viewModel.OpenSettingsRequested += OnOpenSettingsRequested;
        _viewModel.OpenMetadataEditorRequested += OnOpenMetadataEditorRequested;
        _viewModel.OpenDuplicatesWindowRequested += OnOpenDuplicatesWindowRequested;
        _viewModel.OpenAboutWindowRequested += OnOpenAboutWindowRequested;
        _viewModel.OpenWelcomeWizardRequested += OnOpenWelcomeWizardRequested;

        Loaded += async (_, _) => await _viewModel.InitializeAsync();
        Activated += OnMainWindowActivated;
        Closing += OnMainWindowClosing;
        KeyDown += OnMainWindowKeyDown;

        _viewModel.PropertyChanged += (sender, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.IsManagementMode))
            {
                UpdateManagementModeLayout(_viewModel.IsManagementMode);
            }
        };
    }

    private async void OnMainWindowActivated(object? sender, EventArgs e)
    {
        if (_booksOpenedInExternalReader.Count == 0)
            return;

        foreach (long bookId in _booksOpenedInExternalReader.ToList())
        {
            var refreshed = await _viewModel.Database.GetBookByIdAsync(bookId);
            if (refreshed != null)
            {
                _viewModel.UpdateBookProgressFromReader(refreshed);
            }
        }
    }

    private static string? ResolveStandaloneReaderExecutablePath()
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string[] directCandidates =
        [
            Path.Combine(baseDir, "AllReaderBook.exe"),
            Path.Combine(baseDir, "BookHub.Reader.exe")
        ];
        foreach (string candidate in directCandidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        string root = LibraryDatabase.ResolveProjectRoot();
        string[] devCandidates =
        [
            Path.Combine(root, "AllReaderBook", "bin", "Release", "net9.0-windows", "win-x64", "AllReaderBook.exe"),
            Path.Combine(root, "AllReaderBook", "bin", "Release", "net9.0-windows", "AllReaderBook.exe"),
            Path.Combine(root, "AllReaderBook", "bin", "Debug", "net9.0-windows", "AllReaderBook.exe"),
            Path.Combine(root, "BookHub.Reader", "bin", "Release", "net9.0-windows", "win-x64", "AllReaderBook.exe"),
            Path.Combine(root, "BookHub.Reader", "bin", "Release", "net9.0-windows", "AllReaderBook.exe"),
            Path.Combine(root, "BookHub.Reader", "bin", "Debug", "net9.0-windows", "AllReaderBook.exe")
        ];

        return devCandidates.FirstOrDefault(File.Exists);
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

        if (!_viewModel.IsManagementMode)
        {
            s.LeftSidebarWidth = LeftSidebarColumn.ActualWidth > 180 ? LeftSidebarColumn.ActualWidth : 280;
            s.RightInspectorWidth = RightInspectorColumn.ActualWidth > 200 ? RightInspectorColumn.ActualWidth : 330;
        }
        else
        {
            s.LeftSidebarWidth = _savedLeftSidebarWidth > 180 ? _savedLeftSidebarWidth : 280;
            s.RightInspectorWidth = _savedRightInspectorWidth > 200 ? _savedRightInspectorWidth : 330;
        }

        _viewModel.SettingsService.Save();
        _viewModel.Dispose();
    }

    private async void OnOpenReaderRequested(BookRecord book)
    {
        string? standaloneReaderExe = ResolveStandaloneReaderExecutablePath();
        if (!string.IsNullOrWhiteSpace(standaloneReaderExe) && File.Exists(standaloneReaderExe))
        {
            try
            {
                _booksOpenedInExternalReader.Add(book.Id);
                var psi = new ProcessStartInfo
                {
                    FileName = standaloneReaderExe,
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(standaloneReaderExe) ?? AppDomain.CurrentDomain.BaseDirectory
                };
                if (book.CurrentPage > 1)
                {
                    psi.ArgumentList.Add("--page");
                    psi.ArgumentList.Add(book.CurrentPage.ToString());
                }
                psi.ArgumentList.Add(book.FilePath);

                var proc = Process.Start(psi);
                if (proc != null)
                {
                    proc.EnableRaisingEvents = true;
                    proc.Exited += async (_, _) =>
                    {
                        try
                        {
                            var updated = await _viewModel.Database.GetBookByIdAsync(book.Id);
                            if (updated != null)
                            {
                                Dispatcher.Invoke(() => _viewModel.UpdateBookProgressFromReader(updated));
                            }
                        }
                        catch { }
                    };
                    return;
                }
            }
            catch
            {
                // Fallback to embedded reader window if standalone launch fails
            }
        }

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

    private void BooksDataGrid_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject dep)
            return;

        var header = FindVisualParent<DataGridColumnHeader>(dep);
        var presenter = FindVisualParent<DataGridColumnHeadersPresenter>(dep);

        if (header != null || presenter != null)
        {
            ShowColumnVisibilityContextMenu();
            e.Handled = true;
        }
    }

    private void ShowColumnVisibilityContextMenu()
    {
        var menu = new ContextMenu
        {
            Background = (Brush)FindResource("BgSecondaryBrush"),
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            BorderBrush = (Brush)FindResource("BorderSubtleBrush"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4)
        };

        var titleItem = new MenuItem
        {
            Header = "Відображення колонок",
            IsEnabled = false,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("AccentBrush")
        };
        menu.Items.Add(titleItem);
        menu.Items.Add(new Separator());

        foreach (var col in BooksDataGrid.Columns)
        {
            string headerText = col.Header?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(headerText))
                continue;

            var item = new MenuItem
            {
                Header = headerText,
                IsCheckable = true,
                IsChecked = col.Visibility == Visibility.Visible,
                StaysOpenOnClick = true
            };

            var targetCol = col;
            item.Click += (s, ev) =>
            {
                int visibleCount = BooksDataGrid.Columns.Count(c => c.Visibility == Visibility.Visible);
                if (targetCol.Visibility == Visibility.Visible && visibleCount <= 1)
                {
                    item.IsChecked = true;
                    return;
                }

                targetCol.Visibility = item.IsChecked ? Visibility.Visible : Visibility.Collapsed;
                SaveColumnVisibilitySettings();
            };

            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());

        var showAllItem = new MenuItem
        {
            Header = "Показати всі колонки"
        };
        showAllItem.Click += (_, _) =>
        {
            foreach (var c in BooksDataGrid.Columns)
            {
                c.Visibility = Visibility.Visible;
            }
            SaveColumnVisibilitySettings();
        };
        menu.Items.Add(showAllItem);

        menu.PlacementTarget = BooksDataGrid;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private void RestoreTableColumnsState()
    {
        var s = _viewModel.SettingsService.Current;
        if (s.HiddenTableColumns != null && s.HiddenTableColumns.Count > 0)
        {
            var hiddenSet = new HashSet<string>(s.HiddenTableColumns, StringComparer.OrdinalIgnoreCase);
            foreach (var col in BooksDataGrid.Columns)
            {
                string header = col.Header?.ToString() ?? string.Empty;
                if (!string.IsNullOrEmpty(header) && hiddenSet.Contains(header))
                {
                    col.Visibility = Visibility.Collapsed;
                }
                else
                {
                    col.Visibility = Visibility.Visible;
                }
            }
        }
    }

    private void SaveColumnVisibilitySettings()
    {
        var s = _viewModel.SettingsService.Current;
        s.HiddenTableColumns = BooksDataGrid.Columns
            .Where(c => c.Visibility != Visibility.Visible)
            .Select(c => c.Header?.ToString() ?? string.Empty)
            .Where(h => !string.IsNullOrEmpty(h))
            .ToList();
        _viewModel.SettingsService.Save();
    }

    private double _savedLeftSidebarWidth = 280;
    private double _savedRightInspectorWidth = 330;

    private void UpdateManagementModeLayout(bool isManagement)
    {
        if (isManagement)
        {
            if (LeftSidebarColumn.ActualWidth > 80)
                _savedLeftSidebarWidth = LeftSidebarColumn.ActualWidth;
            else if (_viewModel.SettingsService.Current.LeftSidebarWidth > 80)
                _savedLeftSidebarWidth = _viewModel.SettingsService.Current.LeftSidebarWidth;

            if (RightInspectorColumn.ActualWidth > 80)
                _savedRightInspectorWidth = RightInspectorColumn.ActualWidth;
            else if (_viewModel.SettingsService.Current.RightInspectorWidth > 80)
                _savedRightInspectorWidth = _viewModel.SettingsService.Current.RightInspectorWidth;

            LeftSidebarColumn.MinWidth = 0;
            LeftSidebarColumn.Width = new GridLength(0);
            LeftSidebarBorder.Visibility = Visibility.Collapsed;
            LeftSidebarSplitter.Visibility = Visibility.Collapsed;
            if (LeftSidebarSplitterCol != null) LeftSidebarSplitterCol.Width = new GridLength(0);

            RightInspectorColumn.MinWidth = 0;
            RightInspectorColumn.Width = new GridLength(0);
            RightInspectorBorder.Visibility = Visibility.Collapsed;
            RightInspectorSplitter.Visibility = Visibility.Collapsed;
            if (RightInspectorSplitterCol != null) RightInspectorSplitterCol.Width = new GridLength(0);
        }
        else
        {
            var s = _viewModel.SettingsService.Current;
            double leftW = _savedLeftSidebarWidth > 180 ? _savedLeftSidebarWidth : (s.LeftSidebarWidth > 180 ? s.LeftSidebarWidth : 280);
            double rightW = _savedRightInspectorWidth > 200 ? _savedRightInspectorWidth : (s.RightInspectorWidth > 200 ? s.RightInspectorWidth : 330);

            LeftSidebarColumn.MinWidth = 210;
            LeftSidebarColumn.Width = new GridLength(Math.Clamp(leftW, 210, 460));
            LeftSidebarBorder.Visibility = Visibility.Visible;
            LeftSidebarSplitter.Visibility = Visibility.Visible;
            if (LeftSidebarSplitterCol != null) LeftSidebarSplitterCol.Width = new GridLength(5);

            RightInspectorColumn.MinWidth = 240;
            RightInspectorColumn.Width = new GridLength(Math.Clamp(rightW, 240, 520));
            RightInspectorBorder.Visibility = Visibility.Visible;
            RightInspectorSplitter.Visibility = Visibility.Visible;
            if (RightInspectorSplitterCol != null) RightInspectorSplitterCol.Width = new GridLength(5);
        }
    }

    private void OnMainWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _viewModel.IsManagementMode)
        {
            _viewModel.IsManagementMode = false;
            e.Handled = true;
        }
    }

    private void BooksWorkspace_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_viewModel.IsManagementMode && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            if (e.Delta > 0)
            {
                _viewModel.IncreaseManagementZoomCommand.Execute(null);
            }
            else if (e.Delta < 0)
            {
                _viewModel.DecreaseManagementZoomCommand.Execute(null);
            }
        }
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child != null)
        {
            if (child is T parent)
                return parent;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }
}