using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BookHub.App.Services;
using BookHub.Core.Data;
using BookHub.Core.Models;
using BookHub.Core.Parsers;
using BookHub.Core.Pdf;
using BookHub.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace BookHub.App.ViewModels;

public sealed record ZoomPresetOption(string Key, string Label);
public sealed record ViewModePresetOption(string Key, string Label);
public sealed record RecentReaderFileEntry(string FilePath, string FileName, bool FileExists);

public partial class ContinuousPageViewModel : ObservableObject
{
    public int PageNumber { get; init; }
    [ObservableProperty] private BitmapSource? _pageBitmap;
    [ObservableProperty] private string _pageText = string.Empty;
}

public partial class DocumentTabViewModel : ObservableObject, IDisposable
{
    private readonly LibraryDatabase _database;
    private PdfDocumentSession? _pdfSession;
    private List<string> _textPages = [];
    private readonly Dictionary<int, BitmapSource> _pageBitmapCache = [];
    private readonly object _cacheLock = new();
    private CancellationTokenSource? _renderCts;
    private CancellationTokenSource? _resizeDebounceCts;
    private int _lastRenderedTargetPixelWidth;
    private bool _disposed;
    private bool _documentInitialized;
    private bool _isSuspended;
    private bool _isSmartPdfColorFilterEnabled = true;
    private int _lifecycleVersion;

    public BookRecord Book { get; }
    public string TabTitle => Book.Title.Length > 28 ? Book.Title[..25] + "..." : Book.Title;

    public const string MissingTextLayerToolTip = "У цьому документі відсутній текстовий шар (лише зображення). Посимвольне виділення недоступне";
    public const string DefaultTextSelectionToolTip = "Режим виділення тексту";

    [ObservableProperty] private bool _isPdfMode;
    [ObservableProperty] private bool _isTextMode;
    [ObservableProperty] private bool _hasTextLayer = true;
    [ObservableProperty] private bool _isLoading = true;

    public string TextSelectionToolTip => HasTextLayer ? DefaultTextSelectionToolTip : MissingTextLayerToolTip;

    partial void OnHasTextLayerChanged(bool value)
    {
        OnPropertyChanged(nameof(TextSelectionToolTip));
    }
    [ObservableProperty] private string _loadTimeText = string.Empty;

    [ObservableProperty] private ReaderViewMode _viewMode = ReaderViewMode.ContinuousVertical;
    public bool IsSinglePageMode => ViewMode == ReaderViewMode.SinglePage;
    public bool IsTwoPageSpreadMode => ViewMode == ReaderViewMode.TwoPageSpread;
    public bool IsContinuousVerticalMode => ViewMode == ReaderViewMode.ContinuousVertical;

    [ObservableProperty] private int _currentPage = 1;
    [ObservableProperty] private int _totalPages = 1;
    [ObservableProperty] private string _pageJumpInput = "1";
    public double SavedScrollOffset { get; set; }

    // Default Zoom Mode: FitWidth ("Вписати за шириною")
    [ObservableProperty] private string _zoomMode = "FitWidth";
    [ObservableProperty] private double _zoomLevel = 1.0;
    [ObservableProperty] private double _textFontSize = 16.0;

    // Viewport-adaptive dimensions for smooth Fit-to-Width / Fit-to-Page scaling
    [ObservableProperty] private double _viewportWidth = 960;
    [ObservableProperty] private double _viewportHeight = 760;
    [ObservableProperty] private double _computedPageDisplayWidth = 900;
    [ObservableProperty] private double _computedPageDisplayHeight = double.NaN;
    [ObservableProperty] private double _computedSpreadPageDisplayWidth = 440;
    [ObservableProperty] private double _continuousPageItemHeight = 1120;
    [ObservableProperty] private ScrollBarVisibility _horizontalScrollVisibility = ScrollBarVisibility.Disabled;

    [ObservableProperty] private BitmapSource? _currentPageImage;
    [ObservableProperty] private BitmapSource? _secondSpreadPageImage;
    [ObservableProperty] private string _currentPageText = string.Empty;
    [ObservableProperty] private string _secondSpreadPageText = string.Empty;

    [ObservableProperty] private double _readingProgressPercent;
    [ObservableProperty] private string _memoryStatusText = "Кеш: поточна ±1";

    public ObservableCollection<TocItem> TableOfContents { get; } = [];
    public ObservableCollection<AnnotationRecord> Annotations { get; } = [];
    public ObservableCollection<ContinuousPageViewModel> ContinuousWindowPages { get; } = [];

    public string ZoomPercentDisplay => ZoomMode switch
    {
        "FitWidth" => "↔ За шириною",
        "FitPage" => "📄 Ціла стор.",
        _ => $"{Math.Round(ZoomLevel * 100)}%"
    };

    public DocumentTabViewModel(BookRecord book, LibraryDatabase database, string initialZoomMode = "FitWidth", ReaderViewMode initialViewMode = ReaderViewMode.ContinuousVertical, bool isSmartPdfColorFilterEnabled = true)
    {
        Book = book;
        _database = database;
        _viewMode = initialViewMode;
        _isSmartPdfColorFilterEnabled = isSmartPdfColorFilterEnabled;
        CurrentPage = Math.Max(1, book.CurrentPage);
        PageJumpInput = CurrentPage.ToString();
        _zoomMode = string.IsNullOrWhiteSpace(initialZoomMode) ? "FitWidth" : initialZoomMode;
        RecalculatePageLayout();
        SavedScrollOffset = Math.Max(0, (CurrentPage - 1) * ContinuousPageItemHeight);
    }

    public bool IsSmartPdfColorFilterEnabled
    {
        get => _isSmartPdfColorFilterEnabled;
        set
        {
            if (_isSmartPdfColorFilterEnabled == value) return;
            _isSmartPdfColorFilterEnabled = value;
            OnPropertyChanged();
            if (IsPdfMode)
            {
                _ = ClearCacheAndReloadThemeAsync();
            }
        }
    }

    public void ApplyZoomMode(string modeKey)
    {
        if (string.IsNullOrWhiteSpace(modeKey))
            modeKey = "FitWidth";

        ZoomMode = modeKey;
        if (modeKey.EndsWith('%') && double.TryParse(modeKey.TrimEnd('%'), out double pct))
        {
            ZoomLevel = Math.Clamp(pct / 100.0, 0.5, 3.0);
        }

        RecalculatePageLayout();
        OnPropertyChanged(nameof(ZoomPercentDisplay));
        _ = ClearCacheAndReloadThemeAsync();
    }

    public void UpdateViewportSize(double width, double height)
    {
        if (width <= 80 || height <= 80) return;

        ViewportWidth = width;
        ViewportHeight = height;
        RecalculatePageLayout();
        OnPropertyChanged(nameof(ZoomPercentDisplay));

        // Debounce crisp PDFium re-render if target pixel width changed significantly in FitWidth/FitPage mode
        if (IsPdfMode && _pdfSession != null && (ZoomMode == "FitWidth" || ZoomMode == "FitPage"))
        {
            int desiredPx = CalculateTargetRenderPixelWidth();
            if (Math.Abs(desiredPx - _lastRenderedTargetPixelWidth) >= 160)
            {
                _resizeDebounceCts?.Cancel();
                _resizeDebounceCts?.Dispose();
                _resizeDebounceCts = new CancellationTokenSource();
                var token = _resizeDebounceCts.Token;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(150, token);
                        if (!token.IsCancellationRequested)
                        {
                            Application.Current?.Dispatcher.Invoke(() =>
                            {
                                _ = ClearCacheAndReloadThemeAsync();
                            });
                        }
                    }
                    catch (OperationCanceledException) { }
                }, token);
            }
        }
    }

    public void RecalculatePageLayout()
    {
        // Reserve 48px for inner padding and vertical scrollbar so horizontal scrollbar never appears in FitWidth
        double availW = Math.Max(240, ViewportWidth - 48);
        double availH = Math.Max(240, ViewportHeight - 40);

        if (ZoomMode.Equals("FitWidth", StringComparison.OrdinalIgnoreCase))
        {
            HorizontalScrollVisibility = ScrollBarVisibility.Disabled;
            ComputedPageDisplayWidth = availW;
            ComputedPageDisplayHeight = double.NaN;
            ComputedSpreadPageDisplayWidth = Math.Max(180, (availW - 16) / 2.0);
            ZoomLevel = Math.Clamp(availW / 820.0, 0.5, 2.5);
            TextFontSize = Math.Clamp(Math.Round(15.0 + (availW - 600) / 220.0, 1), 13.5, 19.5);
        }
        else if (ZoomMode.Equals("FitPage", StringComparison.OrdinalIgnoreCase))
        {
            HorizontalScrollVisibility = ScrollBarVisibility.Disabled;
            double pageAspect = 612.0 / 792.0;
            if (IsPdfMode && _pdfSession != null)
            {
                var (ptW, ptH) = _pdfSession.GetPageSizePoints(CurrentPage);
                if (ptW > 0 && ptH > 0) pageAspect = ptW / ptH;
            }

            double maxWByHeight = availH * pageAspect;
            ComputedPageDisplayWidth = Math.Min(availW, Math.Max(220, maxWByHeight));
            ComputedPageDisplayHeight = availH;
            ComputedSpreadPageDisplayWidth = Math.Min(Math.Max(180, (availW - 16) / 2.0), Math.Max(180, maxWByHeight));
            ZoomLevel = Math.Clamp(ComputedPageDisplayWidth / 820.0, 0.5, 2.5);
            TextFontSize = 15.0;
        }
        else
        {
            // Fixed percentage (50%, 75%, 100%, 125%, 150%, 200%)
            if (ZoomMode.EndsWith('%') && double.TryParse(ZoomMode.TrimEnd('%'), out double pct))
            {
                ZoomLevel = Math.Clamp(pct / 100.0, 0.5, 3.0);
            }

            double targetW = Math.Round(820.0 * ZoomLevel);
            ComputedPageDisplayWidth = targetW;
            ComputedPageDisplayHeight = double.NaN;
            ComputedSpreadPageDisplayWidth = Math.Round(520.0 * ZoomLevel);
            HorizontalScrollVisibility = targetW > availW ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
            TextFontSize = Math.Clamp(Math.Round(16.0 * ZoomLevel, 1), 11.0, 32.0);
        }

        ContinuousPageItemHeight = Math.Max(360, ComputedPageDisplayWidth * (792.0 / 612.0) + 34);
    }

    private int CalculateTargetRenderPixelWidth()
    {
        // Render at 1.35x display width for super-crisp subpixel text at any FitWidth size
        return Math.Clamp((int)Math.Round(ComputedPageDisplayWidth * 1.35), 720, 2400);
    }

    public async Task InitializeDocumentAsync()
    {
        var sw = Stopwatch.StartNew();
        int lifecycleVersion = Volatile.Read(ref _lifecycleVersion);
        IsLoading = true;
        _isSuspended = false;

        try
        {
            var existingAnns = await _database.GetAnnotationsForBookAsync(Book.Id);
            if (lifecycleVersion != Volatile.Read(ref _lifecycleVersion))
                return;
            Annotations.Clear();
            foreach (var a in existingAnns) Annotations.Add(a);

            string ext = Book.Extension.ToLowerInvariant();
            if (ext == ".pdf")
            {
                IsPdfMode = true;
                IsTextMode = false;

                int initialTargetPage = Math.Clamp(Book.CurrentPage, 1, Math.Max(1, Book.PageCount));
                var (session, toc, hasTextLayer) = await Task.Run(() =>
                {
                    var s = new PdfDocumentSession(Book.FilePath);
                    var t = s.GetTableOfContents();
                    bool hasText = s.HasRecognizedTextLayer(initialTargetPage);
                    return (s, t, hasText);
                });

                if (lifecycleVersion != Volatile.Read(ref _lifecycleVersion))
                {
                    session.Dispose();
                    return;
                }

                _pdfSession = session;
                _documentInitialized = true;
                HasTextLayer = hasTextLayer;
                TotalPages = Math.Max(1, session.PageCount);
                CurrentPage = Math.Clamp(Book.CurrentPage, 1, TotalPages);
                PageJumpInput = CurrentPage.ToString();

                TableOfContents.Clear();
                foreach (var item in toc) TableOfContents.Add(item);

                RecalculatePageLayout();

                sw.Stop();
                LoadTimeText = $"Відкрито за {sw.Elapsed.TotalMilliseconds:0} мс • PDFium Streaming";

                await RefreshActiveViewAsync();
            }
            else
            {
                IsPdfMode = false;
                IsTextMode = true;

                var (pages, toc) = await Task.Run(() => BookFormatParser.LoadDocumentPaginatedContent(Book.FilePath));
                if (lifecycleVersion != Volatile.Read(ref _lifecycleVersion))
                    return;
                _textPages = pages;
                _documentInitialized = true;
                bool isRasterContainer = ext is ".cbz" or ".cbr" or ".djvu";
                HasTextLayer = !isRasterContainer &&
                               _textPages.Any(p => !string.IsNullOrWhiteSpace(p) &&
                                                   p.Any(c => !char.IsWhiteSpace(c) && !char.IsControl(c)));
                TotalPages = Math.Max(1, _textPages.Count);
                CurrentPage = Math.Clamp(Book.CurrentPage, 1, TotalPages);
                PageJumpInput = CurrentPage.ToString();

                TableOfContents.Clear();
                foreach (var item in toc) TableOfContents.Add(item);

                RecalculatePageLayout();

                sw.Stop();
                LoadTimeText = $"Відкрито за {sw.Elapsed.TotalMilliseconds:0} мс • Потоковий {Book.FormatBadge} ({Book.DetectedEncoding})";

                await RefreshActiveViewAsync();
            }

            UpdateProgressMetrics();
        }
        catch (Exception ex)
        {
            _documentInitialized = false;
            _isSuspended = true;
            IsPdfMode = false;
            IsTextMode = true;
            HasTextLayer = false;
            CurrentPageText = $"Помилка під час відкриття документа:\n{ex.Message}";
            LoadTimeText = "Помилка відкриття";
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnViewModeChanged(ReaderViewMode value)
    {
        OnPropertyChanged(nameof(IsSinglePageMode));
        OnPropertyChanged(nameof(IsTwoPageSpreadMode));
        OnPropertyChanged(nameof(IsContinuousVerticalMode));
        RecalculatePageLayout();
        if (value == ReaderViewMode.ContinuousVertical)
        {
            SavedScrollOffset = (CurrentPage - 1) * ContinuousPageItemHeight;
            EnsureContinuousPageItems();
        }
        else
        {
            SavedScrollOffset = 0;
        }
        _ = RefreshActiveViewAsync();
    }

    private void EnsureContinuousPageItems()
    {
        if (ContinuousWindowPages.Count == TotalPages)
            return;

        ContinuousWindowPages.Clear();
        for (int page = 1; page <= TotalPages; page++)
        {
            ContinuousWindowPages.Add(new ContinuousPageViewModel
            {
                PageNumber = page,
                PageText = IsTextMode && page <= _textPages.Count ? _textPages[page - 1] : string.Empty
            });
        }
    }

    public async Task RenderContinuousPagesAsync(int firstVisiblePage, int lastVisiblePage)
    {
        if (!IsContinuousVerticalMode || TotalPages <= 0)
            return;

        int first = Math.Clamp(firstVisiblePage, 1, TotalPages);
        int last = Math.Clamp(lastVisiblePage, first, TotalPages);
        int bufferFirst = Math.Max(1, first - 1);
        int bufferLast = Math.Min(TotalPages, last + 1);

        if (CurrentPage != first)
        {
            CurrentPage = first;
            UpdateProgressMetrics();
        }

        if (!IsPdfMode || _pdfSession == null)
        {
            if (first <= _textPages.Count)
                CurrentPageText = _textPages[first - 1];
            return;
        }

        _renderCts?.Cancel();
        _renderCts?.Dispose();
        _renderCts = new CancellationTokenSource();
        var ct = _renderCts.Token;
        int targetWidthPx = CalculateTargetRenderPixelWidth();
        uint bgArgb = ThemeManagerService.CurrentTheme.PdfCanvasArgb;
        var rendered = new List<(ContinuousPageViewModel Page, BitmapSource? Bitmap)>();

        for (int page = bufferFirst; page <= bufferLast; page++)
        {
            if (ct.IsCancellationRequested)
                return;

            var bitmap = await GetOrRenderPdfBitmapAsync(page, ZoomLevel * 0.95, targetWidthPx, bgArgb, ct);
            rendered.Add((ContinuousWindowPages[page - 1], bitmap));
        }

        foreach (var (page, bitmap) in rendered)
            page.PageBitmap = bitmap;
        for (int page = 1; page <= TotalPages; page++)
        {
            if (page < bufferFirst || page > bufferLast)
                ContinuousWindowPages[page - 1].PageBitmap = null;
        }

        lock (_cacheLock)
        {
            foreach (int cachedPage in _pageBitmapCache.Keys.Where(p => p < bufferFirst || p > bufferLast).ToList())
                _pageBitmapCache.Remove(cachedPage);
            MemoryStatusText = $"Видимі сторінки в RAM: {bufferFirst}–{bufferLast} / {TotalPages}";
        }
    }

    public void SuspendRendering()
    {
        _renderCts?.Cancel();
        _renderCts?.Dispose();
        _renderCts = null;
        lock (_cacheLock)
            _pageBitmapCache.Clear();

        for (int i = 0; i < ContinuousWindowPages.Count; i++)
        {
            ContinuousWindowPages[i].PageBitmap = null;
        }

        CurrentPageImage = null;
        SecondSpreadPageImage = null;
        _isSuspended = true;
        MemoryStatusText = $"Фонова вкладка (стор. {CurrentPage}/{TotalPages}): растри вивантажено";
    }

    public async Task RefreshActiveViewAsync()
    {
        if (IsPdfMode)
        {
            await RenderPdfPagesForCurrentModeAsync();
        }
        else
        {
            UpdateTextPagesForCurrentMode();
        }
    }

    private void UpdateTextPagesForCurrentMode()
    {
        if (_textPages.Count == 0)
        {
            CurrentPageText = "Документ порожній.";
            SecondSpreadPageText = string.Empty;
            return;
        }

        int idx1 = Math.Clamp(CurrentPage - 1, 0, _textPages.Count - 1);
        CurrentPageText = _textPages[idx1];

        int idx2 = idx1 + 1;
        SecondSpreadPageText = idx2 < _textPages.Count ? _textPages[idx2] : "— Кінець документа —";

        if (IsContinuousVerticalMode)
        {
            EnsureContinuousPageItems();
            for (int p = 1; p <= TotalPages; p++)
                ContinuousWindowPages[p - 1].PageText = _textPages[p - 1];
        }

        MemoryStatusText = $"Потокова сторінка {CurrentPage} / {TotalPages}";
    }

    private async Task RenderPdfPagesForCurrentModeAsync()
    {
        if (_pdfSession == null) return;

        if (IsContinuousVerticalMode)
        {
            EnsureContinuousPageItems();
            CurrentPageText = await Task.Run(() => _pdfSession.ExtractPageText(CurrentPage));
            await RenderContinuousPagesAsync(CurrentPage, Math.Min(TotalPages, CurrentPage + 2));
            return;
        }

        _renderCts?.Cancel();
        _renderCts?.Dispose();
        _renderCts = new CancellationTokenSource();
        var ct = _renderCts.Token;

        int targetPage = CurrentPage;
        double zoom = ZoomLevel;
        int targetWidthPx = CalculateTargetRenderPixelWidth();
        _lastRenderedTargetPixelWidth = targetWidthPx;
        uint bgArgb = ThemeManagerService.CurrentTheme.PdfCanvasArgb;

        CurrentPageImage = await GetOrRenderPdfBitmapAsync(targetPage, zoom, targetWidthPx, bgArgb, ct);
        if (ct.IsCancellationRequested) return;

        if (_pdfSession != null)
        {
            try
            {
                CurrentPageText = await Task.Run(() => _pdfSession.ExtractPageText(targetPage), ct);
                if (IsTwoPageSpreadMode && targetPage + 1 <= TotalPages)
                {
                    SecondSpreadPageText = await Task.Run(() => _pdfSession.ExtractPageText(targetPage + 1), ct);
                }
            }
            catch { /* Text layer optional */ }
        }

        if (IsTwoPageSpreadMode)
        {
            if (targetPage + 1 <= TotalPages)
                SecondSpreadPageImage = await GetOrRenderPdfBitmapAsync(targetPage + 1, zoom * 0.92, (int)(targetWidthPx * 0.75), bgArgb, ct);
            else
                SecondSpreadPageImage = null;
        }
        // Keep only the active page and its immediate neighbors cached.
        lock (_cacheLock)
        {
            var keysToRemove = _pageBitmapCache.Keys
                .Where(k => k < targetPage - 1 || k > targetPage + 2)
                .ToList();
            foreach (var k in keysToRemove)
                _pageBitmapCache.Remove(k);
        }

        // Preload adjacent pages only in paged reading modes.
        int[] neighbors = [targetPage + 1, targetPage - 1];
        foreach (int n in neighbors)
        {
            if (ct.IsCancellationRequested) return;
            if (n < 1 || n > TotalPages) continue;
            await GetOrRenderPdfBitmapAsync(n, zoom, targetWidthPx, bgArgb, ct);
        }

        lock (_cacheLock)
        {
            var activePages = string.Join(", ", _pageBitmapCache.Keys.OrderBy(x => x));
            MemoryStatusText = $"Активні сторінки в RAM: [{activePages}]";
        }
    }

    private async Task<BitmapSource?> GetOrRenderPdfBitmapAsync(int pageNumber, double zoom, int maxWidthPx, uint bgArgb, CancellationToken ct)
    {
        if (_pdfSession == null || pageNumber < 1 || pageNumber > TotalPages) return null;

        lock (_cacheLock)
        {
            if (_pageBitmapCache.TryGetValue(pageNumber, out var existing))
                return existing;
        }

        try
        {
            bool applySmartFilter = _isSmartPdfColorFilterEnabled && bgArgb != 0xFFFFFFFFu;
            var buf = await Task.Run(() => _pdfSession.RenderPageBgra(pageNumber, zoom, maxWidthPx, bgArgb, applySmartFilter), ct);
            if (ct.IsCancellationRequested) return null;

            var bmp = BitmapSource.Create(buf.Width, buf.Height, 96, 96, PixelFormats.Bgra32, null, buf.Pixels, buf.Stride);
            bmp.Freeze();

            lock (_cacheLock)
            {
                _pageBitmapCache[pageNumber] = bmp;
            }
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    public string GetCurrentPageExtractText()
    {
        if (IsPdfMode && _pdfSession != null)
        {
            return _pdfSession.ExtractPageText(CurrentPage);
        }
        return CurrentPageText;
    }

    public async Task<List<int>> FindPagesMatchingQueryAsync(string query, CancellationToken ct = default)
    {
        var matches = new List<int>();
        if (string.IsNullOrWhiteSpace(query) || !HasTextLayer)
            return matches;

        string q = query.Trim();
        return await Task.Run(() =>
        {
            if (IsPdfMode && _pdfSession != null)
            {
                for (int p = 1; p <= TotalPages; p++)
                {
                    if (ct.IsCancellationRequested) break;
                    string text = _pdfSession.ExtractPageText(p);
                    if (!string.IsNullOrEmpty(text) && text.Contains(q, StringComparison.OrdinalIgnoreCase))
                    {
                        matches.Add(p);
                    }
                }
            }
            else
            {
                for (int i = 0; i < _textPages.Count; i++)
                {
                    if (ct.IsCancellationRequested) break;
                    if (!string.IsNullOrEmpty(_textPages[i]) && _textPages[i].Contains(q, StringComparison.OrdinalIgnoreCase))
                    {
                        matches.Add(i + 1);
                    }
                }
            }
            return matches;
        }, ct);
    }

    private void UpdateProgressMetrics()
    {
        PageJumpInput = CurrentPage.ToString();
        ReadingProgressPercent = TotalPages > 0
            ? Math.Clamp(Math.Round((double)CurrentPage / TotalPages * 100.0, 1), 0, 100)
            : 0;
        Book.CurrentPage = CurrentPage;
        Book.PageCount = TotalPages;
        Book.ReadingProgress = ReadingProgressPercent;
    }

    public async Task GoToPageAsync(int pageNumber)
    {
        int clamped = Math.Clamp(pageNumber, 1, Math.Max(1, TotalPages));
        CurrentPage = clamped;
        UpdateProgressMetrics();
        if (ZoomMode == "FitPage")
        {
            RecalculatePageLayout();
        }
        await RefreshActiveViewAsync();
    }

    public async Task ClearCacheAndReloadThemeAsync()
    {
        lock (_cacheLock)
        {
            _pageBitmapCache.Clear();
        }
        await RefreshActiveViewAsync();
    }

    public async Task SaveProgressAsync()
    {
        UpdateProgressMetrics();
        await _database.UpdateReadingProgressAsync(Book.Id, CurrentPage, TotalPages);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _resizeDebounceCts?.Cancel();
        _resizeDebounceCts?.Dispose();
        _renderCts?.Cancel();
        _renderCts?.Dispose();
        lock (_cacheLock) { _pageBitmapCache.Clear(); }
        _pdfSession?.Dispose();
    }

    public bool IsDocumentInitialized => _documentInitialized;
    public bool IsSuspended => _isSuspended;
}

public partial class ReaderViewModel : ObservableObject, IDisposable
{
    private const int MaximumOpenTabs = 20;
    private readonly LibraryDatabase _database;
    private readonly UserSettingsService _settingsService;
    private double _lastKnownViewportWidth = 960;
    private double _lastKnownViewportHeight = 760;
    private bool _wasTocVisibleBeforeFullScreen = true;
    private bool _wasNotesVisibleBeforeFullScreen = true;
    private bool _suppressViewModePresetSync;
    private CancellationTokenSource? _searchCts;
    private List<int> _matchedPages = [];
    private int _currentMatchedPageIndex = -1;
    private bool _disposed;

    public event Action? FocusViewportRequested;

    public ObservableCollection<DocumentTabViewModel> OpenTabs { get; } = [];
    public ObservableCollection<RecentReaderFileEntry> RecentFiles { get; } = [];
    public bool HasRecentFiles => RecentFiles.Count > 0;

    public IReadOnlyList<ZoomPresetOption> ZoomPresets { get; } =
    [
        new("FitWidth", "За шириною"),
        new("FitPage", "Повна сторінка"),
        new("50%", "50%"),
        new("75%", "75%"),
        new("100%", "100%"),
        new("125%", "125%"),
        new("150%", "150%"),
        new("200%", "200%")
    ];

    public IReadOnlyList<ViewModePresetOption> ViewModePresets { get; } =
    [
        new("ContinuousVertical", "Безперервний"),
        new("SinglePage", "1 Стор."),
        new("TwoPageSpread", "Розворот"),
        new("SplitView", "◫ Split-View")
    ];

    [ObservableProperty] private DocumentTabViewModel? _activeTab;
    [ObservableProperty] private DocumentTabViewModel? _secondarySplitTab;
    [ObservableProperty] private ZoomPresetOption _selectedZoomPreset;
    [ObservableProperty] private ViewModePresetOption _selectedViewModePreset;
    [ObservableProperty] private bool _isSplitViewEnabled;
    [ObservableProperty] private bool _isFullScreen;
    [ObservableProperty] private bool _isTocVisible = true;
    [ObservableProperty] private bool _isAnnotationsVisible = true;
    [ObservableProperty] private bool _isTextSelectionMode;
    [ObservableProperty] private bool _isSmartPdfColorFilterEnabled = true;

    // In-document search state
    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private int _searchTotalMatches;
    [ObservableProperty] private int _searchCurrentMatchNumber;
    public bool HasSearchQuery => !string.IsNullOrEmpty(SearchQuery);
    public string SearchStatusDisplay => SearchTotalMatches > 0
        ? $"{SearchCurrentMatchNumber} / {SearchTotalMatches}"
        : "0 / 0";

    public bool CanSelectText => ActiveTab is { HasTextLayer: true };
    public string TextSelectionToolTip => ActiveTab != null
        ? ActiveTab.TextSelectionToolTip
        : DocumentTabViewModel.DefaultTextSelectionToolTip;

    partial void OnIsTextSelectionModeChanged(bool value)
    {
        if (value && !CanSelectText)
        {
            _isTextSelectionMode = false;
            OnPropertyChanged(nameof(IsTextSelectionMode));
        }
    }

    [RelayCommand]
    public void ToggleTextSelectionMode()
    {
        if (!CanSelectText)
        {
            IsTextSelectionMode = false;
            return;
        }

        IsTextSelectionMode = !IsTextSelectionMode;
    }

    // New Annotation Input State
    [ObservableProperty] private string _newQuoteInput = string.Empty;
    [ObservableProperty] private string _newCommentInput = string.Empty;
    [ObservableProperty] private string _selectedHighlightColor = "#FACC15";
    [ObservableProperty] private string _selectedThemeName;

    public IReadOnlyList<string> ThemeNames { get; } = ThemeManagerService.Presets.Select(p => p.Name).ToList();
    public string[] HighlightColors { get; } = ["#FACC15", "#34D399", "#38BDF8", "#F472B6", "#A78BFA"];

    public ReaderViewModel(BookRecord initialBook, LibraryDatabase database, UserSettingsService settingsService)
    {
        _database = database;
        _settingsService = settingsService;
        _selectedThemeName = _settingsService.Current.ThemeName;
        _isSmartPdfColorFilterEnabled = _settingsService.Current.IsSmartPdfColorFilterEnabled;

        string savedZoomMode = string.IsNullOrWhiteSpace(_settingsService.Current.ReaderZoomMode)
            ? "FitWidth"
            : _settingsService.Current.ReaderZoomMode;

        _selectedZoomPreset = ZoomPresets.FirstOrDefault(z => z.Key.Equals(savedZoomMode, StringComparison.OrdinalIgnoreCase))
                              ?? ZoomPresets[0];

        ThemeManagerService.ThemeChanged += OnGlobalThemeChanged;

        ReaderViewMode initialViewMode = Enum.TryParse(_settingsService.Current.LastReaderViewMode, true, out ReaderViewMode savedViewMode) &&
                                         Enum.IsDefined(savedViewMode)
            ? savedViewMode
            : ReaderViewMode.ContinuousVertical;
        _selectedViewModePreset = ViewModePresets.FirstOrDefault(v => v.Key.Equals(initialViewMode.ToString(), StringComparison.OrdinalIgnoreCase))
                                  ?? ViewModePresets[0];

        AddRecentFile(initialBook.FilePath);
        RefreshRecentFilesValidation();

        var firstTab = new DocumentTabViewModel(initialBook, _database, _selectedZoomPreset.Key, initialViewMode, _isSmartPdfColorFilterEnabled);
        OpenTabs.Add(firstTab);
        ActiveTab = firstTab;
        SecondarySplitTab = firstTab;
    }

    partial void OnIsSmartPdfColorFilterEnabledChanged(bool value)
    {
        _settingsService.Current.IsSmartPdfColorFilterEnabled = value;
        _settingsService.Save();
        foreach (var tab in OpenTabs)
        {
            tab.IsSmartPdfColorFilterEnabled = value;
        }
        FocusViewportRequested?.Invoke();
    }

    partial void OnSelectedViewModePresetChanged(ViewModePresetOption value)
    {
        if (value == null || _suppressViewModePresetSync)
            return;

        if (value.Key == "SplitView")
        {
            if (!IsSplitViewEnabled)
                ToggleSplitView();
        }
        else
        {
            if (IsSplitViewEnabled)
                IsSplitViewEnabled = false;
            SetViewMode(value.Key);
        }

        FocusViewportRequested?.Invoke();
    }

    private void SyncSelectedViewModePreset(ViewModePresetOption preset)
    {
        if (ReferenceEquals(SelectedViewModePreset, preset))
            return;

        _suppressViewModePresetSync = true;
        try
        {
            SelectedViewModePreset = preset;
        }
        finally
        {
            _suppressViewModePresetSync = false;
        }
    }

    public void AddRecentFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;
        string normalized = Path.GetFullPath(filePath);
        var list = _settingsService.Current.RecentReaderFiles;
        list.RemoveAll(p => string.Equals(p, normalized, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, normalized);
        if (list.Count > 15)
            list.RemoveRange(15, list.Count - 15);
        _settingsService.Save();
        RefreshRecentFilesValidation();
    }

    public void RefreshRecentFilesValidation()
    {
        RecentFiles.Clear();
        foreach (string path in _settingsService.Current.RecentReaderFiles)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            RecentFiles.Add(new RecentReaderFileEntry(path, Path.GetFileName(path), File.Exists(path)));
        }
        OnPropertyChanged(nameof(HasRecentFiles));
    }

    [RelayCommand]
    private async Task OpenRecentFileFromToolbarAsync(RecentReaderFileEntry? entry)
    {
        if (entry == null || !File.Exists(entry.FilePath)) return;
        using var scanner = new FileScannerService(_database);
        var rec = await scanner.ProcessSingleFileAsync(entry.FilePath) ?? await _database.GetBookByPathAsync(entry.FilePath);
        if (rec != null)
        {
            await OpenBookInNewTabAsync(rec);
        }
    }

    [RelayCommand]
    private void ClearRecentFiles()
    {
        _settingsService.Current.RecentReaderFiles.Clear();
        _settingsService.Save();
        RefreshRecentFilesValidation();
    }

    partial void OnSearchQueryChanged(string value)
    {
        OnPropertyChanged(nameof(HasSearchQuery));
        _ = ExecuteDocumentSearchAsync(value);
    }

    private async Task ExecuteDocumentSearchAsync(string query)
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        if (string.IsNullOrWhiteSpace(query) || ActiveTab == null || !ActiveTab.HasTextLayer)
        {
            _matchedPages.Clear();
            _currentMatchedPageIndex = -1;
            SearchTotalMatches = 0;
            SearchCurrentMatchNumber = 0;
            OnPropertyChanged(nameof(SearchStatusDisplay));
            return;
        }

        var pages = await ActiveTab.FindPagesMatchingQueryAsync(query, ct);
        if (ct.IsCancellationRequested) return;

        _matchedPages = pages;
        SearchTotalMatches = _matchedPages.Count;
        if (_matchedPages.Count > 0)
        {
            _currentMatchedPageIndex = 0;
            SearchCurrentMatchNumber = 1;
            OnPropertyChanged(nameof(SearchStatusDisplay));
            await ActiveTab.GoToPageAsync(_matchedPages[0]);
        }
        else
        {
            _currentMatchedPageIndex = -1;
            SearchCurrentMatchNumber = 0;
            OnPropertyChanged(nameof(SearchStatusDisplay));
        }
    }

    [RelayCommand]
    private async Task NextSearchMatchAsync()
    {
        if (_matchedPages.Count == 0 || ActiveTab == null) return;
        _currentMatchedPageIndex = (_currentMatchedPageIndex + 1) % _matchedPages.Count;
        SearchCurrentMatchNumber = _currentMatchedPageIndex + 1;
        OnPropertyChanged(nameof(SearchStatusDisplay));
        await ActiveTab.GoToPageAsync(_matchedPages[_currentMatchedPageIndex]);
    }

    [RelayCommand]
    private async Task PreviousSearchMatchAsync()
    {
        if (_matchedPages.Count == 0 || ActiveTab == null) return;
        _currentMatchedPageIndex = (_currentMatchedPageIndex - 1 + _matchedPages.Count) % _matchedPages.Count;
        SearchCurrentMatchNumber = _currentMatchedPageIndex + 1;
        OnPropertyChanged(nameof(SearchStatusDisplay));
        await ActiveTab.GoToPageAsync(_matchedPages[_currentMatchedPageIndex]);
    }

    [RelayCommand]
    private void ClearSearch()
    {
        SearchQuery = string.Empty;
    }

    private void OnActiveTabInternalPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DocumentTabViewModel.HasTextLayer) or nameof(DocumentTabViewModel.TextSelectionToolTip))
        {
            OnPropertyChanged(nameof(CanSelectText));
            OnPropertyChanged(nameof(TextSelectionToolTip));
            if (!CanSelectText && IsTextSelectionMode)
            {
                IsTextSelectionMode = false;
            }
        }
    }

    partial void OnActiveTabChanged(DocumentTabViewModel? value)
    {
        OnPropertyChanged(nameof(CanSelectText));
        OnPropertyChanged(nameof(TextSelectionToolTip));
        if (!CanSelectText && IsTextSelectionMode)
        {
            IsTextSelectionMode = false;
        }

        if (value == null) return;
        value.PropertyChanged += OnActiveTabInternalPropertyChanged;
        value.UpdateViewportSize(_lastKnownViewportWidth, _lastKnownViewportHeight);
        if (value.IsDocumentInitialized)
            _ = value.RefreshActiveViewAsync();
        else if (value.IsSuspended)
            _ = value.InitializeDocumentAsync();
        var match = ZoomPresets.FirstOrDefault(z => z.Key.Equals(value.ZoomMode, StringComparison.OrdinalIgnoreCase));
        if (match != null && match != SelectedZoomPreset)
        {
            _selectedZoomPreset = match;
            OnPropertyChanged(nameof(SelectedZoomPreset));
        }
    }

    partial void OnActiveTabChanging(DocumentTabViewModel? oldValue, DocumentTabViewModel? newValue)
    {
        if (oldValue != null)
        {
            oldValue.PropertyChanged -= OnActiveTabInternalPropertyChanged;
            if (oldValue != newValue)
                oldValue.SuspendRendering();
        }
    }

    partial void OnSelectedZoomPresetChanged(ZoomPresetOption value)
    {
        if (value == null) return;
        _settingsService.Current.ReaderZoomMode = value.Key;
        _settingsService.Save();

        foreach (var tab in OpenTabs)
        {
            tab.ApplyZoomMode(value.Key);
        }
    }

    public void UpdateViewportDimensions(double width, double height)
    {
        if (width <= 80 || height <= 80) return;
        _lastKnownViewportWidth = width;
        _lastKnownViewportHeight = height;

        foreach (var tab in OpenTabs)
        {
            tab.UpdateViewportSize(width, height);
        }
    }

    private void OnGlobalThemeChanged(ThemePreset preset)
    {
        foreach (var tab in OpenTabs)
        {
            if (tab.IsSuspended)
                continue;
            _ = tab.ClearCacheAndReloadThemeAsync();
        }
    }

    partial void OnSelectedThemeNameChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            ThemeManagerService.ApplyTheme(value);
            _settingsService.Current.ThemeName = value;
            _settingsService.Save();
        }
    }

    public async Task InitializeAllTabsAsync()
    {
        foreach (var tab in OpenTabs)
        {
            await tab.InitializeDocumentAsync();
        }
    }

    public async Task OpenBookInNewTabAsync(BookRecord book)
    {
        var existing = OpenTabs.FirstOrDefault(t => t.Book.Id == book.Id);
        if (existing != null)
        {
            ActiveTab = existing;
            return;
        }

        if (OpenTabs.Count >= MaximumOpenTabs)
        {
            MessageBox.Show($"Досягнуто ліміту: одночасно можна відкрити не більше {MaximumOpenTabs} вкладок.",
                "Ліміт вкладок", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Always open new document in the active ZoomMode (default FitWidth) and current viewport size
        AddRecentFile(book.FilePath);
        ReaderViewMode selectedMode = ActiveTab?.ViewMode ?? ParseSavedViewMode();
        var newTab = new DocumentTabViewModel(book, _database, SelectedZoomPreset?.Key ?? "FitWidth", selectedMode, IsSmartPdfColorFilterEnabled);
        newTab.UpdateViewportSize(_lastKnownViewportWidth, _lastKnownViewportHeight);
        OpenTabs.Add(newTab);
        ActiveTab = newTab;
        if (SecondarySplitTab == null || SecondarySplitTab == OpenTabs[0])
            SecondarySplitTab = newTab;

        await newTab.InitializeDocumentAsync();
    }

    [RelayCommand]
    private async Task OpenAnotherFileInTabAsync()
    {
        if (OpenTabs.Count >= MaximumOpenTabs)
        {
            MessageBox.Show($"Досягнуто ліміту: одночасно можна відкрити не більше {MaximumOpenTabs} вкладок.",
                "Ліміт вкладок", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new OpenFileDialog
        {
            Title = "Відкрити книгу або документ у новій вкладці",
            Filter = BookFormatParser.GetOpenFileDialogFilter()
        };

        if (dlg.ShowDialog() == true && File.Exists(dlg.FileName))
        {
            if (!BookFormatParser.ValidateFileFormatSignature(dlg.FileName, out string err))
            {
                MessageBox.Show(err, "ReaderHub", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            using var scanner = new FileScannerService(_database);
            var rec = await scanner.ProcessSingleFileAsync(dlg.FileName) ?? await _database.GetBookByPathAsync(dlg.FileName);
            if (rec != null)
            {
                await OpenBookInNewTabAsync(rec);
            }
        }
    }

    [RelayCommand]
    private async Task CloseTabAsync(DocumentTabViewModel? tab)
    {
        if (tab == null || !OpenTabs.Contains(tab)) return;
        int index = OpenTabs.IndexOf(tab);
        await tab.SaveProgressAsync();
        tab.Dispose();
        OpenTabs.Remove(tab);
        if (ActiveTab == tab)
            ActiveTab = OpenTabs.Count > 0 ? OpenTabs[Math.Min(index, OpenTabs.Count - 1)] : null;
        if (SecondarySplitTab == tab)
            SecondarySplitTab = OpenTabs.FirstOrDefault();
    }

    [RelayCommand]
    private async Task CloseOtherTabsAsync(DocumentTabViewModel? keepTab)
    {
        if (keepTab == null || !OpenTabs.Contains(keepTab))
            return;

        foreach (var tab in OpenTabs.Where(t => t != keepTab).ToList())
        {
            await tab.SaveProgressAsync();
            tab.Dispose();
            OpenTabs.Remove(tab);
        }

        ActiveTab = keepTab;
        SecondarySplitTab = keepTab;
    }

    [RelayCommand]
    private async Task CloseAllTabsAsync()
    {
        foreach (var tab in OpenTabs.ToList())
        {
            await tab.SaveProgressAsync();
            tab.Dispose();
        }

        OpenTabs.Clear();
        ActiveTab = null;
        SecondarySplitTab = null;
    }

    [RelayCommand]
    private void SetViewMode(string mode)
    {
        if (ActiveTab == null) return;
        ActiveTab.ViewMode = mode switch
        {
            "TwoPageSpread" => ReaderViewMode.TwoPageSpread,
            "ContinuousVertical" => ReaderViewMode.ContinuousVertical,
            _ => ReaderViewMode.SinglePage
        };
        _settingsService.Current.LastReaderViewMode = ActiveTab.ViewMode.ToString();
        _settingsService.Save();

        if (!IsSplitViewEnabled)
        {
            string modeKey = ActiveTab.ViewMode.ToString();
            var match = ViewModePresets.FirstOrDefault(v => v.Key.Equals(modeKey, StringComparison.OrdinalIgnoreCase));
            if (match != null)
                SyncSelectedViewModePreset(match);
        }
    }

    private ReaderViewMode ParseSavedViewMode() =>
        Enum.TryParse(_settingsService.Current.LastReaderViewMode, true, out ReaderViewMode mode) && Enum.IsDefined(mode)
            ? mode
            : ReaderViewMode.ContinuousVertical;

    [RelayCommand]
    private void SetZoomPreset(string presetKey)
    {
        var target = ZoomPresets.FirstOrDefault(z => z.Key.Equals(presetKey, StringComparison.OrdinalIgnoreCase));
        if (target != null)
        {
            SelectedZoomPreset = target;
        }
    }

    [RelayCommand]
    private void ToggleSplitView()
    {
        IsSplitViewEnabled = !IsSplitViewEnabled;
        if (IsSplitViewEnabled && SecondarySplitTab == null)
        {
            SecondarySplitTab = OpenTabs.LastOrDefault() ?? ActiveTab;
        }

        string targetKey = IsSplitViewEnabled ? "SplitView" : (ActiveTab?.ViewMode.ToString() ?? "ContinuousVertical");
        var match = ViewModePresets.FirstOrDefault(v => v.Key.Equals(targetKey, StringComparison.OrdinalIgnoreCase));
        if (match != null)
            SyncSelectedViewModePreset(match);
    }

    [RelayCommand]
    private void ToggleFullScreen()
    {
        if (!IsFullScreen)
        {
            _wasTocVisibleBeforeFullScreen = IsTocVisible;
            _wasNotesVisibleBeforeFullScreen = IsAnnotationsVisible;

            IsFullScreen = true;
            IsTocVisible = false;
            IsAnnotationsVisible = false;
        }
        else
        {
            IsFullScreen = false;
            IsTocVisible = _wasTocVisibleBeforeFullScreen;
            IsAnnotationsVisible = _wasNotesVisibleBeforeFullScreen;
        }
    }

    [RelayCommand]
    private void ToggleToc() => IsTocVisible = !IsTocVisible;

    [RelayCommand]
    private void ToggleAnnotations() => IsAnnotationsVisible = !IsAnnotationsVisible;

    [RelayCommand]
    private async Task NextPageAsync()
    {
        if (ActiveTab == null) return;
        int step = ActiveTab.IsTwoPageSpreadMode ? 2 : 1;
        await ActiveTab.GoToPageAsync(ActiveTab.CurrentPage + step);
    }

    [RelayCommand]
    private async Task PreviousPageAsync()
    {
        if (ActiveTab == null) return;
        int step = ActiveTab.IsTwoPageSpreadMode ? 2 : 1;
        await ActiveTab.GoToPageAsync(ActiveTab.CurrentPage - step);
    }

    [RelayCommand]
    private async Task FirstPageAsync()
    {
        if (ActiveTab != null) await ActiveTab.GoToPageAsync(1);
    }

    [RelayCommand]
    private async Task LastPageAsync()
    {
        if (ActiveTab != null) await ActiveTab.GoToPageAsync(ActiveTab.TotalPages);
    }

    [RelayCommand]
    private async Task JumpToInputPageAsync()
    {
        if (ActiveTab == null) return;
        if (int.TryParse(ActiveTab.PageJumpInput, out int target))
            await ActiveTab.GoToPageAsync(target);
        else
            ActiveTab.PageJumpInput = ActiveTab.CurrentPage.ToString();
    }

    [RelayCommand]
    private void ZoomIn()
    {
        string[] steps = ["50%", "75%", "100%", "125%", "150%", "200%"];
        int curIdx = Array.FindIndex(steps, s => s.Equals(SelectedZoomPreset?.Key, StringComparison.OrdinalIgnoreCase));
        if (curIdx < 0) curIdx = 2; // From FitWidth/FitPage jump to 125%
        int nextIdx = Math.Min(steps.Length - 1, curIdx + 1);
        SetZoomPreset(steps[nextIdx]);
    }

    [RelayCommand]
    private void ZoomOut()
    {
        string[] steps = ["50%", "75%", "100%", "125%", "150%", "200%"];
        int curIdx = Array.FindIndex(steps, s => s.Equals(SelectedZoomPreset?.Key, StringComparison.OrdinalIgnoreCase));
        if (curIdx < 0) curIdx = 2; // From FitWidth/FitPage jump to 75%
        int prevIdx = Math.Max(0, curIdx - 1);
        SetZoomPreset(steps[prevIdx]);
    }

    [RelayCommand]
    private void SetHighlightColor(string colorHex)
    {
        if (!string.IsNullOrWhiteSpace(colorHex))
            SelectedHighlightColor = colorHex;
    }

    [RelayCommand]
    private async Task AddQuickBookmarkAsync()
    {
        if (ActiveTab == null) return;
        string rawSelection = !string.IsNullOrWhiteSpace(NewQuoteInput) ? NewQuoteInput.Trim() : string.Empty;
        string conciseQuote;

        if (!string.IsNullOrWhiteSpace(rawSelection))
        {
            string normalized = string.Join(" ", rawSelection.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries));
            conciseQuote = normalized.Length > 45 ? normalized[..45].TrimEnd() + "..." : normalized;
        }
        else
        {
            string pageText = ActiveTab.GetCurrentPageExtractText();
            string[] words = pageText.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
            {
                conciseQuote = $"Стор. {ActiveTab.CurrentPage}";
            }
            else
            {
                string snippet = string.Join(" ", words.Take(Math.Min(4, words.Length)));
                if (snippet.Length > 42) snippet = snippet[..42].TrimEnd();
                conciseQuote = $"Стор. {ActiveTab.CurrentPage} — {snippet}...";
            }
        }

        var ann = new AnnotationRecord
        {
            BookId = ActiveTab.Book.Id,
            PageNumber = ActiveTab.CurrentPage,
            ScrollOffset = ActiveTab.SavedScrollOffset,
            QuoteText = conciseQuote,
            CommentText = "Швидка закладка",
            HighlightColor = "#38BDF8",
            IsBookmark = true,
            CreatedAt = DateTime.Now
        };

        ann.Id = await _database.AddAnnotationAsync(ann);
        ActiveTab.Annotations.Add(ann);
        IsAnnotationsVisible = true;
    }

    [RelayCommand]
    private async Task AddHighlightOrNoteAsync()
    {
        if (ActiveTab == null) return;

        string quote = NewQuoteInput.Trim();
        if (string.IsNullOrWhiteSpace(quote))
        {
            string autoExtract = ActiveTab.GetCurrentPageExtractText();
            string[] words = autoExtract.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries);
            quote = words.Length > 0
                ? $"Стор. {ActiveTab.CurrentPage} — {string.Join(" ", words.Take(Math.Min(5, words.Length)))}..."
                : $"Стор. {ActiveTab.CurrentPage}";
        }
        else if (quote.Length > 120)
        {
            quote = quote[..120].TrimEnd() + "...";
        }

        var ann = new AnnotationRecord
        {
            BookId = ActiveTab.Book.Id,
            PageNumber = ActiveTab.CurrentPage,
            ScrollOffset = ActiveTab.SavedScrollOffset,
            QuoteText = quote,
            CommentText = NewCommentInput.Trim(),
            HighlightColor = SelectedHighlightColor,
            IsBookmark = false,
            CreatedAt = DateTime.Now
        };

        ann.Id = await _database.AddAnnotationAsync(ann);
        ActiveTab.Annotations.Add(ann);
        NewQuoteInput = string.Empty;
        NewCommentInput = string.Empty;
        FocusViewportRequested?.Invoke();
    }

    [RelayCommand]
    private async Task DeleteAnnotationAsync(AnnotationRecord? ann)
    {
        if (ann == null || ActiveTab == null) return;
        await _database.DeleteAnnotationAsync(ann.Id);
        ActiveTab.Annotations.Remove(ann);
    }

    [RelayCommand]
    private void ExportAnnotationsToObsidianMd()
    {
        if (ActiveTab == null) return;

        var dlg = new SaveFileDialog
        {
            Title = "Експорт нотаток та витягів у Markdown (Obsidian)",
            Filter = "Markdown файли (*.md)|*.md",
            FileName = $"{ActiveTab.Book.Title}_Notes.md"
        };

        if (dlg.ShowDialog() == true)
        {
            ExportAndBackupService.ExportAnnotationsToMarkdown(ActiveTab.Book, ActiveTab.Annotations, dlg.FileName);
            MessageBox.Show($"Конспект та анотації збережено у файл:\n{dlg.FileName}", "Експорт для Obsidian", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    [RelayCommand]
    private async Task ExportCurrentPagesToPdfAsync()
    {
        if (ActiveTab == null) return;

        var dlg = new SaveFileDialog
        {
            Title = "Експорт сторінок у PDF",
            Filter = "PDF Документ (*.pdf)|*.pdf",
            FileName = $"{ActiveTab.Book.Title}_Pages_{ActiveTab.CurrentPage}-{Math.Min(ActiveTab.TotalPages, ActiveTab.CurrentPage + 4)}.pdf"
        };

        if (dlg.ShowDialog() == true)
        {
            int start = ActiveTab.CurrentPage;
            int end = Math.Min(ActiveTab.TotalPages, start + 4);
            await Task.Run(() => ExportAndBackupService.ExportPagesToPdf(ActiveTab.Book, start, end, dlg.FileName));
            MessageBox.Show($"Сторінки {start}–{end} успішно експортовано у PDF:\n{dlg.FileName}", "Експорт PDF", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    [RelayCommand]
    private void PrintCurrentPage()
    {
        if (ActiveTab == null) return;
        try
        {
            var pd = new PrintDialog();
            if (pd.ShowDialog() == true)
            {
                if (ActiveTab.IsPdfMode && ActiveTab.CurrentPageImage != null)
                {
                    var img = new System.Windows.Controls.Image
                    {
                        Source = ActiveTab.CurrentPageImage,
                        Stretch = Stretch.Uniform
                    };
                    pd.PrintVisual(img, $"BookHub Print — {ActiveTab.Book.Title} (Page {ActiveTab.CurrentPage})");
                }
                else
                {
                    var tb = new TextBlock
                    {
                        Text = ActiveTab.CurrentPageText,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(48),
                        FontSize = 12
                    };
                    pd.PrintVisual(tb, $"BookHub Print — {ActiveTab.Book.Title}");
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Помилка друку: {ex.Message}", "Друк", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private void OpenInSystemApp()
    {
        if (ActiveTab == null || !File.Exists(ActiveTab.Book.FilePath)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = ActiveTab.Book.FilePath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Не вдалося відкрити у зовнішній програмі:\n{ex.Message}", "Помилка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public async Task SaveAllTabsProgressAsync()
    {
        foreach (var tab in OpenTabs)
        {
            await tab.SaveProgressAsync();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ThemeManagerService.ThemeChanged -= OnGlobalThemeChanged;
        foreach (var tab in OpenTabs)
        {
            tab.Dispose();
        }
    }
}
