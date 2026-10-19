using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BookHub.App.ViewModels;
using BookHub.Core.Models;
using BookHub.Core.Services;

namespace BookHub.App.Views;

public partial class ReaderWindow : Window
{
    private readonly ReaderViewModel _viewModel;
    private readonly UserSettingsService _settingsService;
    private readonly Func<Task>? _onClosedCallback;
    private DocumentTabViewModel? _observedTab;

    public ReaderWindow(ReaderViewModel viewModel, UserSettingsService settingsService, Func<Task>? onClosedCallback = null)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _settingsService = settingsService;
        _onClosedCallback = onClosedCallback;
        DataContext = _viewModel;
        _viewModel.OpenTabs.CollectionChanged += (_, _) => Dispatcher.BeginInvoke(new Action(UpdateTabOverflowIndicators));
        ObserveActiveTab(_viewModel.ActiveTab);

        RestoreReaderWindowGeometry();
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        Loaded += async (_, _) =>
        {
            if (PrimaryViewportHost.ActualWidth > 80 && PrimaryViewportHost.ActualHeight > 80)
            {
                _viewModel.UpdateViewportDimensions(PrimaryViewportHost.ActualWidth, PrimaryViewportHost.ActualHeight);
            }
            await _viewModel.InitializeAllTabsAsync();
            UpdateTabOverflowIndicators();
            RestoreActiveTabScrollPosition();
        };
        Closing += OnWindowClosing;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ReaderViewModel.IsTocVisible))
        {
            ApplyTocColumnCollapseState(_viewModel.IsTocVisible);
        }
        else if (e.PropertyName == nameof(ReaderViewModel.IsAnnotationsVisible))
        {
            ApplyNotesColumnCollapseState(_viewModel.IsAnnotationsVisible);
        }
        else if (e.PropertyName == nameof(ReaderViewModel.ActiveTab))
        {
            ObserveActiveTab(_viewModel.ActiveTab);
            Dispatcher.BeginInvoke(new Action(RestoreActiveTabScrollPosition));
        }
    }

    private void ObserveActiveTab(DocumentTabViewModel? tab)
    {
        if (_observedTab != null)
            _observedTab.PropertyChanged -= OnActiveTabPropertyChanged;

        _observedTab = tab;
        if (_observedTab != null)
            _observedTab.PropertyChanged += OnActiveTabPropertyChanged;
    }

    private void OnActiveTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentTabViewModel.CurrentPage) ||
            e.PropertyName == nameof(DocumentTabViewModel.ViewMode) ||
            (e.PropertyName == nameof(DocumentTabViewModel.IsLoading) && sender is DocumentTabViewModel { IsLoading: false }))
        {
            Dispatcher.BeginInvoke(new Action(RestoreActiveTabScrollPosition));
        }
    }

    private void ApplyTocColumnCollapseState(bool isTocVisible)
    {
        if (isTocVisible)
        {
            double restoredWidth = Math.Clamp(_settingsService.Current.ReaderTocWidth, 180, 500);
            TocSidebarColumn.MinWidth = 180;
            TocSidebarColumn.MaxWidth = 500;
            TocSidebarColumn.Width = new GridLength(restoredWidth);
            TocSplitterColumn.Width = new GridLength(5);
        }
        else
        {
            if (TocSidebarColumn.ActualWidth >= 180)
            {
                _settingsService.Current.ReaderTocWidth = Math.Clamp(TocSidebarColumn.ActualWidth, 180, 500);
            }
            TocSidebarColumn.MinWidth = 0;
            TocSidebarColumn.MaxWidth = 0;
            TocSidebarColumn.Width = new GridLength(0);
            TocSplitterColumn.Width = new GridLength(0);
        }
    }

    private void ApplyNotesColumnCollapseState(bool isNotesVisible)
    {
        if (isNotesVisible)
        {
            double restoredWidth = Math.Clamp(_settingsService.Current.ReaderNotesWidth, 200, 600);
            NotesSidebarColumn.MinWidth = 200;
            NotesSidebarColumn.MaxWidth = 600;
            NotesSidebarColumn.Width = new GridLength(restoredWidth);
            NotesSplitterColumn.Width = new GridLength(5);
        }
        else
        {
            if (NotesSidebarColumn.ActualWidth >= 200)
            {
                _settingsService.Current.ReaderNotesWidth = Math.Clamp(NotesSidebarColumn.ActualWidth, 200, 600);
            }
            NotesSidebarColumn.MinWidth = 0;
            NotesSidebarColumn.MaxWidth = 0;
            NotesSidebarColumn.Width = new GridLength(0);
            NotesSplitterColumn.Width = new GridLength(0);
        }
    }

    private void PrimaryViewportHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width > 80 && e.NewSize.Height > 80)
        {
            _viewModel.UpdateViewportDimensions(e.NewSize.Width, e.NewSize.Height);
        }

        if (_viewModel.IsTocVisible && TocSidebarColumn.ActualWidth >= 180)
        {
            _settingsService.Current.ReaderTocWidth = Math.Clamp(TocSidebarColumn.ActualWidth, 180, 500);
        }
    }

    private void RestoreReaderWindowGeometry()
    {
        var workArea = SystemParameters.WorkArea;
        MaxHeight = workArea.Height + 14;
        MaxWidth = workArea.Width + 14;

        var s = _settingsService.Current;

        // Restore TOC Sidebar Width (clamped to [180, 500])
        double tocWidth = Math.Clamp(s.ReaderTocWidth, 180, 500);
        TocSidebarColumn.MinWidth = 180;
        TocSidebarColumn.MaxWidth = 500;
        TocSidebarColumn.Width = new GridLength(tocWidth);

        // Restore Notes Sidebar Width (clamped to [200, 600])
        double notesWidth = Math.Clamp(s.ReaderNotesWidth, 200, 600);
        NotesSidebarColumn.MinWidth = 200;
        NotesSidebarColumn.MaxWidth = 600;
        NotesSidebarColumn.Width = new GridLength(notesWidth);
        ApplyNotesColumnCollapseState(_viewModel.IsAnnotationsVisible);

        if (s.ReaderWindowWidth >= 700 && s.ReaderWindowHeight >= 500)
        {
            Width = Math.Min(s.ReaderWindowWidth, workArea.Width);
            Height = Math.Min(s.ReaderWindowHeight, workArea.Height);
        }

        // Off-screen protection (handles disconnected monitors or resolution changes)
        bool hasValidPosition = !double.IsNaN(s.ReaderWindowLeft) &&
                                !double.IsNaN(s.ReaderWindowTop) &&
                                s.ReaderWindowLeft >= SystemParameters.VirtualScreenLeft - 50 &&
                                s.ReaderWindowTop >= SystemParameters.VirtualScreenTop - 50 &&
                                s.ReaderWindowLeft + 200 <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth &&
                                s.ReaderWindowTop + 200 <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;

        if (hasValidPosition)
        {
            Left = Math.Clamp(s.ReaderWindowLeft, workArea.Left, Math.Max(workArea.Left, workArea.Right - Width));
            Top = Math.Clamp(s.ReaderWindowTop, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - Height));
        }
        else
        {
            Left = workArea.Left + (workArea.Width - Width) / 2;
            Top = workArea.Top + (workArea.Height - Height) / 2;
        }

        WindowState = s.ReaderWindowState == "Maximized" ? WindowState.Maximized : WindowState.Normal;
    }

    private void SaveReaderWindowGeometry()
    {
        var s = _settingsService.Current;
        s.ReaderWindowState = WindowState == WindowState.Maximized ? "Maximized" : "Normal";
        if (WindowState == WindowState.Normal)
        {
            s.ReaderWindowWidth = Width;
            s.ReaderWindowHeight = Height;
            s.ReaderWindowLeft = Left;
            s.ReaderWindowTop = Top;
        }

        if (_viewModel.IsTocVisible && TocSidebarColumn.ActualWidth >= 180)
        {
            s.ReaderTocWidth = Math.Clamp(TocSidebarColumn.ActualWidth, 180, 500);
        }

        if (_viewModel.IsAnnotationsVisible && NotesSidebarColumn.ActualWidth >= 200)
        {
            s.ReaderNotesWidth = Math.Clamp(NotesSidebarColumn.ActualWidth, 200, 600);
        }

        _settingsService.Save();
    }

    private async void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        try
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            SaveReaderWindowGeometry();
            await _viewModel.SaveAllTabsProgressAsync();
            _viewModel.Dispose();
            if (_onClosedCallback != null)
            {
                await _onClosedCallback();
            }
        }
        catch
        {
            // Ignore shutdown errors
        }
    }

    private async void TocTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TocItem tocItem && tocItem.PageNumber > 0 && _viewModel.ActiveTab != null)
        {
            await _viewModel.ActiveTab.GoToPageAsync(tocItem.PageNumber);
            ScrollReaderToCurrentPage();
        }
    }

    private async void AnnotationsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is AnnotationRecord ann && _viewModel.ActiveTab != null)
        {
            await _viewModel.ActiveTab.GoToPageAsync(ann.PageNumber);
            ScrollReaderToCurrentPage();
        }
    }

    private void ReaderTextBox_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && !string.IsNullOrWhiteSpace(tb.SelectedText) && tb.SelectedText.Length >= 2)
        {
            var text = tb.SelectedText.Trim();
            _viewModel.NewQuoteInput = text;
            _viewModel.IsAnnotationsVisible = true;
            try
            {
                Clipboard.SetText(text);
            }
            catch { /* Ignore transient clipboard access lock */ }
        }
    }

    private async void PageJumpTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await _viewModel.JumpToInputPageCommand.ExecuteAsync(null);
            ScrollReaderToCurrentPage();
        }
    }

    private void PageSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SliderPreviewText != null)
        {
            SliderPreviewText.Text = $"Стор. {(int)Math.Round(e.NewValue)}";
        }
    }

    private async void PageSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.ActiveTab != null)
        {
            int target = (int)Math.Round(PageSlider.Value);
            await _viewModel.ActiveTab.GoToPageAsync(target);
            ScrollReaderToCurrentPage();
        }
    }

    private void PdfScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            if (e.Delta > 0)
                _viewModel.ZoomInCommand.Execute(null);
            else
                _viewModel.ZoomOutCommand.Execute(null);
        }
    }

    private void TabStripScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e) =>
        UpdateTabOverflowIndicators();

    private void TabStripScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateTabOverflowIndicators();

    private void TabStripScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (TabStripScrollViewer.ScrollableWidth <= 0)
            return;

        e.Handled = true;
        TabStripScrollViewer.ScrollToHorizontalOffset(
            Math.Clamp(TabStripScrollViewer.HorizontalOffset - e.Delta, 0, TabStripScrollViewer.ScrollableWidth));
    }

    private void UpdateTabOverflowIndicators()
    {
        if (TabStripScrollViewer == null || TabListBox == null || !TabStripScrollViewer.IsLoaded)
            return;

        int hiddenLeft = 0;
        int hiddenRight = 0;
        double viewportWidth = TabStripScrollViewer.ViewportWidth;
        for (int i = 0; i < _viewModel.OpenTabs.Count; i++)
        {
            if (TabListBox.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem item)
                continue;

            Rect bounds = item.TransformToAncestor(TabStripScrollViewer)
                .TransformBounds(new Rect(new Point(), item.RenderSize));
            if (bounds.Right <= 0)
                hiddenLeft++;
            else if (bounds.Left >= viewportWidth)
                hiddenRight++;
        }

        LeftTabOverflowButton.Content = $"◀ {hiddenLeft}";
        RightTabOverflowButton.Content = $"{hiddenRight} ▶";
        LeftTabOverflowButton.Visibility = hiddenLeft > 0 ? Visibility.Visible : Visibility.Collapsed;
        RightTabOverflowButton.Visibility = hiddenRight > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void LeftTabOverflowButton_Click(object sender, RoutedEventArgs e) =>
        await ScrollTabStripToHiddenTabAsync(left: true);

    private async void RightTabOverflowButton_Click(object sender, RoutedEventArgs e) =>
        await ScrollTabStripToHiddenTabAsync(left: false);

    private async Task ScrollTabStripToHiddenTabAsync(bool left)
    {
        double distance = 0;
        for (int i = 0; i < _viewModel.OpenTabs.Count; i++)
        {
            if (TabListBox.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem item)
                continue;

            Rect bounds = item.TransformToAncestor(TabStripScrollViewer)
                .TransformBounds(new Rect(new Point(), item.RenderSize));
            if (left && bounds.Right <= 0)
                distance = item.ActualWidth;
            else if (!left && bounds.Left >= TabStripScrollViewer.ViewportWidth)
            {
                distance = item.ActualWidth;
                break;
            }
        }

        if (distance <= 0)
            return;

        double from = TabStripScrollViewer.HorizontalOffset;
        double to = Math.Clamp(from + (left ? -distance : distance), 0, TabStripScrollViewer.ScrollableWidth);
        const int steps = 10;
        for (int step = 1; step <= steps; step++)
        {
            TabStripScrollViewer.ScrollToHorizontalOffset(from + (to - from) * step / steps);
            await Task.Delay(12);
        }
    }

    private void ContinuousListBox_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is not ListBox listBox || listBox.DataContext is not DocumentTabViewModel tab ||
            FindVisualChild<ScrollViewer>(listBox) is not { } scrollViewer || e.ExtentHeightChange == 0 && e.VerticalChange == 0)
            return;

        tab.SavedScrollOffset = scrollViewer.VerticalOffset;
        double stride = Math.Max(1, tab.ContinuousPageItemHeight);
        int firstVisible = Math.Clamp((int)(scrollViewer.VerticalOffset / stride) + 1, 1, tab.TotalPages);
        int lastVisible = Math.Clamp((int)Math.Ceiling((scrollViewer.VerticalOffset + scrollViewer.ViewportHeight) / stride) + 1, firstVisible, tab.TotalPages);
        _ = tab.RenderContinuousPagesAsync(firstVisible, lastVisible);
    }

    private void PagedScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is ScrollViewer scrollViewer &&
            scrollViewer.DataContext is DocumentTabViewModel tab &&
            e.VerticalChange != 0)
        {
            tab.SavedScrollOffset = scrollViewer.VerticalOffset;
        }
    }

    private void RestoreActiveTabScrollPosition()
    {
        if (_viewModel.ActiveTab is not { } tab)
            return;

        if (tab.IsContinuousVerticalMode)
        {
            ListBox listBox = tab.IsPdfMode ? PdfContinuousListBox : TextContinuousListBox;
            if (tab.CurrentPage > 0 && tab.CurrentPage <= tab.ContinuousWindowPages.Count)
                listBox.ScrollIntoView(tab.ContinuousWindowPages[tab.CurrentPage - 1]);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (FindVisualChild<ScrollViewer>(listBox) is { } scrollViewer)
                    scrollViewer.ScrollToVerticalOffset(tab.SavedScrollOffset);
            }));
        }
        else if (tab.IsPdfMode)
        {
            PdfScrollViewer.ScrollToVerticalOffset(tab.SavedScrollOffset);
        }
        else
        {
            TextScrollViewer.ScrollToVerticalOffset(tab.SavedScrollOffset);
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
                return match;

            if (FindVisualChild<T>(child) is { } descendant)
                return descendant;
        }

        return null;
    }

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.W)
        {
            e.Handled = true;
            await _viewModel.CloseTabCommand.ExecuteAsync(_viewModel.ActiveTab);
            return;
        }

        if (Keyboard.FocusedElement is TextBox)
            return;

        if (e.Key is Key.F11 or Key.F)
        {
            e.Handled = true;
            _viewModel.ToggleFullScreenCommand.Execute(null);
            WindowState = _viewModel.IsFullScreen ? WindowState.Maximized : WindowState.Normal;
            return;
        }

        if (e.Key == Key.Escape && _viewModel.IsFullScreen)
        {
            e.Handled = true;
            _viewModel.ToggleFullScreenCommand.Execute(null);
            return;
        }

        if (e.Key is Key.Right or Key.PageDown)
        {
            e.Handled = true;
            await _viewModel.NextPageCommand.ExecuteAsync(null);
            ScrollReaderToCurrentPage();
        }
        else if (e.Key is Key.Left or Key.PageUp)
        {
            e.Handled = true;
            await _viewModel.PreviousPageCommand.ExecuteAsync(null);
            ScrollReaderToCurrentPage();
        }
        else if (e.Key == Key.Home)
        {
            e.Handled = true;
            await _viewModel.FirstPageCommand.ExecuteAsync(null);
            ScrollReaderToCurrentPage();
        }
        else if (e.Key == Key.End)
        {
            e.Handled = true;
            await _viewModel.LastPageCommand.ExecuteAsync(null);
            ScrollReaderToCurrentPage();
        }
    }

    private void ScrollReaderToCurrentPage()
    {
        if (_viewModel.ActiveTab is not { } tab)
            return;

        if (tab.IsContinuousVerticalMode)
        {
            ListBox listBox = tab.IsPdfMode ? PdfContinuousListBox : TextContinuousListBox;
            if (tab.CurrentPage > 0 && tab.CurrentPage <= tab.ContinuousWindowPages.Count)
                listBox.ScrollIntoView(tab.ContinuousWindowPages[tab.CurrentPage - 1]);
        }
        else if (tab.IsPdfMode)
        {
            PdfScrollViewer.ScrollToTop();
        }
        else
        {
            TextScrollViewer.ScrollToTop();
        }
    }
}
