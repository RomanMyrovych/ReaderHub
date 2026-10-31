using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using BookHub.App.Services;
using BookHub.Core.Data;
using BookHub.Core.Models;
using BookHub.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace BookHub.App.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly LibraryDatabase _database;
    private readonly FileScannerService _scanner;
    private readonly UserSettingsService _settingsService;
    private readonly List<BookRecord> _allBooks = [];
    private HashSet<long>? _activeFtsMatchedIds;
    private HashSet<long>? _activeShelfBookIds;
    private SmartCollectionRecord? _activeSmartCollection;
    private int _sidebarFilterSelectionVersion;
    private int _filterApplicationVersion;
    private CancellationTokenSource? _scanCts;
    private bool _suppressAutoFilter;
    private bool _suppressSidebarFilterSelectionChanges;
    private bool _isUpdatingFilteredBooks;
    private bool _disposed;

    public LibraryDatabase Database => _database;
    public FileScannerService Scanner => _scanner;
    public UserSettingsService SettingsService => _settingsService;

    public ObservableCollection<BookRecord> FilteredBooks { get; } = [];
    public ObservableCollection<string> Categories { get; } = ["Усі категорії"];
    public ObservableCollection<string> Authors { get; } = ["Усі автори"];
    public ObservableCollection<string> AvailableTags { get; } = ["Усі теги"];
    public ObservableCollection<VirtualShelfRecord> VirtualShelves { get; } = [];
    public ObservableCollection<VirtualShelfRecord> AssignableShelves { get; } = [];
    public ObservableCollection<SmartCollectionRecord> SmartCollections { get; } = [];
    public ObservableCollection<RelatedItemRecord> SelectedBookRelatedItems { get; } = [];

    public IReadOnlyList<string> ThemeNames { get; } = ThemeManagerService.Presets.Select(p => p.Name).ToList();
    public string[] SortOptions { get; } = ["Recent", "Title", "Author", "Pages", "Size", "Year"];
    public string[] RelationTypes { get; } = ["Конспект", "Код / Репозиторій", "Шпаргалка", "Пов'язана книга"];

    [ObservableProperty] private BookRecord? _selectedBook;
    [ObservableProperty] private VirtualShelfRecord? _selectedBookShelf;
    private bool _isUpdatingSelectedBookShelf;
    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private bool _useFullTextFtsSearch = true;

    [ObservableProperty] private string _selectedFormat = "ALL";
    [ObservableProperty] private string _selectedStatusFilter = "ALL";
    [ObservableProperty] private string _selectedCategory = "Усі категорії";
    [ObservableProperty] private string _selectedAuthor = "Усі автори";
    [ObservableProperty] private string _selectedTag = "Усі теги";
    [ObservableProperty] private string _selectedSortOrder = "Recent";
    [ObservableProperty] private string _selectedThemeName = "Obsidian Dark";

    [ObservableProperty] private bool _isTableViewMode;
    public bool IsGridViewMode => !IsTableViewMode;

    [ObservableProperty] private VirtualShelfRecord? _selectedShelf;
    [ObservableProperty] private SmartCollectionRecord? _selectedSmartCollection;
    [ObservableProperty] private string _newShelfNameInput = string.Empty;

    // Related item creation inputs
    [ObservableProperty] private string _newRelatedTitleInput = string.Empty;
    [ObservableProperty] private string _newRelatedTypeInput = "Конспект";
    [ObservableProperty] private string _newRelatedTargetInput = string.Empty;

    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _statusMessage = "Готово";
    [ObservableProperty] private int _totalBooksCount;
    [ObservableProperty] private int _filteredBooksCount;

    public event Action<BookRecord>? OpenReaderRequested;
    public event Action? OpenSettingsRequested;
    public event Action<List<BookRecord>>? OpenMetadataEditorRequested;
    public event Action? OpenDuplicatesWindowRequested;
    public event Action? OpenAboutWindowRequested;
    public event Action? OpenWelcomeWizardRequested;

    public MainViewModel()
    {
        _database = new LibraryDatabase();
        _settingsService = new UserSettingsService();
        _scanner = new FileScannerService(_database);
        _scanner.BookAddedOrUpdated += OnScannerBookAddedOrUpdated;
        _scanner.ScanProgressChanged += OnScannerProgressChanged;

        var s = _settingsService.Current;
        _selectedThemeName = s.ThemeName;
        _isTableViewMode = s.IsTableViewMode;

        // Default filter states: ALL formats active, ALL reading statuses active
        _selectedFormat = "ALL";
        _selectedStatusFilter = "ALL";
        _selectedCategory = "Усі категорії";
        _selectedAuthor = "Усі автори";
        _selectedTag = "Усі теги";
        _selectedSortOrder = string.IsNullOrWhiteSpace(s.SortOrder) ? "Recent" : s.SortOrder;

        _scanner.ScanEbooksEnabled = s.ScanEbooksEnabled;
        _scanner.ScanDocumentsEnabled = s.ScanDocumentsEnabled;
        _scanner.EnableBackgroundFileWatcher = s.EnableBackgroundFileWatcher;
        _scanner.IsManagedLibraryEnabled = s.IsManagedLibraryEnabled;
        _scanner.ManagedLibraryMoveFiles = s.ManagedLibraryMoveFiles;
        _scanner.ManagedLibraryPath = s.GetResolvedManagedLibraryPath();

        ThemeManagerService.ApplyTheme(_selectedThemeName);
        LocalizationService.ApplyLanguage(s.LanguageCode);
    }

    public void ReloadSettingsFromWizard()
    {
        _settingsService.Load();
        var s = _settingsService.Current;
        SelectedThemeName = s.ThemeName;
        IsTableViewMode = s.IsTableViewMode;

        _scanner.ScanEbooksEnabled = s.ScanEbooksEnabled;
        _scanner.ScanDocumentsEnabled = s.ScanDocumentsEnabled;
        _scanner.EnableBackgroundFileWatcher = s.EnableBackgroundFileWatcher;
        _scanner.IsManagedLibraryEnabled = s.IsManagedLibraryEnabled;
        _scanner.ManagedLibraryMoveFiles = s.ManagedLibraryMoveFiles;
        _scanner.ManagedLibraryPath = s.GetResolvedManagedLibraryPath();

        ThemeManagerService.ApplyTheme(SelectedThemeName);
        LocalizationService.ApplyLanguage(s.LanguageCode);
    }

    public async Task InitializeAsync()
    {
        await _database.InitializeAsync();

        await ReloadShelvesAndSmartCollectionsAsync();
        await ReloadLibraryFromDatabaseAsync();
        await _scanner.RefreshWatchersAsync();

        _ = RunFullScanAsync();
    }

    public async Task ReloadShelvesAndSmartCollectionsAsync()
    {
        var shelves = await _database.GetVirtualShelvesAsync();

        // 1. Sidebar Virtual Shelves with "Усі книги / Всі полиці" reset option at the top
        VirtualShelves.Clear();
        VirtualShelves.Add(new VirtualShelfRecord
        {
            Id = 0,
            Name = "Усі книги / Всі полиці",
            Icon = "📁"
        });
        foreach (var sh in shelves) VirtualShelves.Add(sh);

        // 2. Assignable Shelves for Inspector with removal option
        AssignableShelves.Clear();
        AssignableShelves.Add(new VirtualShelfRecord
        {
            Id = 0,
            Name = "— Видалити з полички —",
            Icon = "✕"
        });
        foreach (var sh in shelves) AssignableShelves.Add(sh);

        // 3. Smart Collections with "Усі книги / Без колекцій" reset option at the top
        var smarts = await _database.GetSmartCollectionsAsync();
        SmartCollections.Clear();
        SmartCollections.Add(new SmartCollectionRecord
        {
            Id = 0,
            Name = "Усі книги / Без колекцій",
            Icon = "⚡"
        });
        foreach (var sc in smarts) SmartCollections.Add(sc);
    }

    public async Task ReloadLibraryFromDatabaseAsync()
    {
        long? keepSelectedId = SelectedBook?.Id;

        var books = await _database.GetAllBooksAsync();
        _allBooks.Clear();
        _allBooks.AddRange(books);
        TotalBooksCount = _allBooks.Count;

        // Rebuilding facet filter collections must not fire concurrent (racy) filter passes.
        _suppressAutoFilter = true;
        RebuildFacetFilters();
        _suppressAutoFilter = false;

        await ApplyFilterAsync();

        // Keep the previously selected book active so the right inspector panel never empties.
        if (keepSelectedId.HasValue)
        {
            var restored = _allBooks.FirstOrDefault(b => b.Id == keepSelectedId.Value);
            if (restored != null)
            {
                SelectedBook = restored;
            }
        }
    }

    private void RebuildFacetFilters()
    {
        string prevCat = SelectedCategory;
        string prevAuth = SelectedAuthor;
        string prevTag = SelectedTag;

        var cats = _allBooks
            .Select(b => b.Category)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c)
            .ToList();

        Categories.Clear();
        Categories.Add("Усі категорії");
        foreach (var c in cats) Categories.Add(c);
        SelectedCategory = Categories.Contains(prevCat) ? prevCat : "Усі категорії";

        var authors = _allBooks
            .Select(b => b.Author)
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => a)
            .ToList();

        Authors.Clear();
        Authors.Add("Усі автори");
        foreach (var a in authors) Authors.Add(a);
        SelectedAuthor = Authors.Contains(prevAuth) ? prevAuth : "Усі автори";

        var tags = _allBooks
            .SelectMany(b => (b.Tags ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t)
            .ToList();

        AvailableTags.Clear();
        AvailableTags.Add("Усі теги");
        foreach (var t in tags) AvailableTags.Add(t);
        SelectedTag = AvailableTags.Contains(prevTag) ? prevTag : "Усі теги";
    }

    partial void OnSelectedBookChanged(BookRecord? value)
    {
        if (_isUpdatingFilteredBooks) return;
        _ = LoadRelatedItemsForSelectedBookAsync(value);

        _isUpdatingSelectedBookShelf = true;
        try
        {
            if (value?.VirtualShelfId.HasValue == true && value.VirtualShelfId.Value > 0)
            {
                SelectedBookShelf = AssignableShelves.FirstOrDefault(s => s.Id == value.VirtualShelfId.Value);
            }
            else
            {
                SelectedBookShelf = null;
            }
        }
        finally
        {
            _isUpdatingSelectedBookShelf = false;
        }
    }

    partial void OnSelectedBookShelfChanged(VirtualShelfRecord? value)
    {
        if (_isUpdatingSelectedBookShelf || SelectedBook == null) return;
        _ = ApplyShelfChangeToSelectedBookAsync(value);
    }

    private async Task ApplyShelfChangeToSelectedBookAsync(VirtualShelfRecord? shelf)
    {
        if (SelectedBook == null) return;
        var book = SelectedBook;

        if (shelf == null || shelf.Id == 0)
        {
            book.VirtualShelfId = null;
            book.VirtualShelfName = null;
            await _database.RemoveBookFromShelfAsync(book.Id);
            StatusMessage = $"Книгу «{book.Title}» видалено з полички";
        }
        else
        {
            book.VirtualShelfId = shelf.Id;
            book.VirtualShelfName = shelf.Name;
            await _database.AssignBookToShelfAsync(book.Id, shelf.Id);
            StatusMessage = $"Книгу «{book.Title}» призначено на поличку «{shelf.Name}»";
        }

        // Refresh shelf counts in left sidebar
        var freshShelves = await _database.GetVirtualShelvesAsync();
        foreach (var fresh in freshShelves)
        {
            var existing = VirtualShelves.FirstOrDefault(s => s.Id == fresh.Id);
            if (existing != null) existing.BookCount = fresh.BookCount;
        }

        SelectedBook = book;
    }

    private async Task LoadRelatedItemsForSelectedBookAsync(BookRecord? book)
    {
        SelectedBookRelatedItems.Clear();
        if (book == null) return;
        var items = await _database.GetRelatedItemsForBookAsync(book.Id);
        foreach (var item in items) SelectedBookRelatedItems.Add(item);
    }

    partial void OnSearchQueryChanged(string value)
    {
        if (!_suppressAutoFilter) _ = ApplyFilterAsync();
    }

    partial void OnUseFullTextFtsSearchChanged(bool value)
    {
        if (!_suppressAutoFilter) _ = ApplyFilterAsync();
    }

    partial void OnSelectedFormatChanged(string value)
    {
        _settingsService.Current.SelectedFormat = value;
        _settingsService.Save();
        if (!_suppressAutoFilter) _ = ApplyFilterAsync();
    }

    partial void OnSelectedStatusFilterChanged(string value)
    {
        _settingsService.Current.SelectedStatusFilter = value;
        _settingsService.Save();
        if (!_suppressAutoFilter) _ = ApplyFilterAsync();
    }

    partial void OnSelectedCategoryChanged(string value)
    {
        _settingsService.Current.SelectedCategory = value;
        _settingsService.Save();
        if (!_suppressAutoFilter) _ = ApplyFilterAsync();
    }

    partial void OnSelectedAuthorChanged(string value)
    {
        _settingsService.Current.SelectedAuthor = value;
        _settingsService.Save();
        if (!_suppressAutoFilter) _ = ApplyFilterAsync();
    }

    partial void OnSelectedTagChanged(string value)
    {
        _settingsService.Current.SelectedTag = value;
        _settingsService.Save();
        if (!_suppressAutoFilter) _ = ApplyFilterAsync();
    }

    partial void OnSelectedSortOrderChanged(string value)
    {
        _settingsService.Current.SortOrder = value;
        _settingsService.Save();
        if (!_suppressAutoFilter) _ = ApplyFilterAsync();
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

    partial void OnIsTableViewModeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsGridViewMode));
        _settingsService.Current.IsTableViewMode = value;
        _settingsService.Save();
    }

    partial void OnSelectedShelfChanged(VirtualShelfRecord? value)
    {
        if (_suppressSidebarFilterSelectionChanges) return;
        _ = ApplyShelfSelectionAsync(value);
    }

    private async Task ApplyShelfSelectionAsync(VirtualShelfRecord? shelf)
    {
        int selectionVersion = ++_sidebarFilterSelectionVersion;
        _suppressSidebarFilterSelectionChanges = true;
        try
        {
            _activeSmartCollection = null;
            SelectedSmartCollection = null;
        }
        finally
        {
            _suppressSidebarFilterSelectionChanges = false;
        }

        if (shelf == null || shelf.Id == 0)
        {
            _activeShelfBookIds = null;
        }
        else
        {
            var bookIds = await _database.GetBookIdsInShelfAsync(shelf.Id);
            if (selectionVersion != _sidebarFilterSelectionVersion) return;
            _activeShelfBookIds = bookIds;
        }

        if (selectionVersion != _sidebarFilterSelectionVersion) return;
        await ApplyFilterAsync();
    }

    partial void OnSelectedSmartCollectionChanged(SmartCollectionRecord? value)
    {
        if (_suppressSidebarFilterSelectionChanges) return;
        _ = ApplySmartCollectionSelectionAsync(value);
    }

    private async Task ApplySmartCollectionSelectionAsync(SmartCollectionRecord? collection)
    {
        ++_sidebarFilterSelectionVersion;
        _suppressSidebarFilterSelectionChanges = true;
        try
        {
            _activeShelfBookIds = null;
            SelectedShelf = null;
        }
        finally
        {
            _suppressSidebarFilterSelectionChanges = false;
        }

        _activeSmartCollection = collection is { Id: > 0 } ? collection : null;
        await ApplyFilterAsync();
    }

    public async Task ApplyFilterAsync()
    {
        int filterVersion = ++_filterApplicationVersion;
        IEnumerable<BookRecord> query = _allBooks;

        // 1. Virtual Shelf filter
        if (_activeShelfBookIds != null)
        {
            query = query.Where(b => _activeShelfBookIds.Contains(b.Id));
        }

        // 2. Smart Collection dynamic rule filter
        if (_activeSmartCollection != null)
        {
            var sc = _activeSmartCollection;
            if (!string.IsNullOrWhiteSpace(sc.KeywordRule))
            {
                query = query.Where(b =>
                    b.Title.Contains(sc.KeywordRule, StringComparison.OrdinalIgnoreCase) ||
                    b.Tags.Contains(sc.KeywordRule, StringComparison.OrdinalIgnoreCase) ||
                    b.Category.Contains(sc.KeywordRule, StringComparison.OrdinalIgnoreCase));
            }
            if (!string.IsNullOrWhiteSpace(sc.FormatRule))
            {
                query = query.Where(b => b.Extension.Equals(sc.FormatRule, StringComparison.OrdinalIgnoreCase));
            }
            if (sc.StatusRule.HasValue)
            {
                query = query.Where(b => (int)b.Status == sc.StatusRule.Value);
            }
            if (sc.AddedThisYearOnly == 1)
            {
                int currentYear = DateTime.Now.Year;
                query = query.Where(b => b.AddedAt.Year == currentYear);
            }
        }

        // 3. Format filter (Default: ALL formats active; if none selected or ALL, show all books)
        bool hasFormatFilter = !string.IsNullOrWhiteSpace(SelectedFormat) &&
                               !SelectedFormat.Equals("ALL", StringComparison.OrdinalIgnoreCase) &&
                               !SelectedFormat.Equals("Усі", StringComparison.OrdinalIgnoreCase) &&
                               !SelectedFormat.Equals("Усі (All)", StringComparison.OrdinalIgnoreCase);

        if (hasFormatFilter)
        {
            if (SelectedFormat.Equals("DOCX", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(b => b.Extension is ".docx" or ".doc" or ".odt" or ".rtf" or ".txt" or ".html" or ".htm" or ".md" or ".pptx" or ".xlsx");
            }
            else
            {
                string extTarget = "." + SelectedFormat.ToLowerInvariant().TrimStart('.');
                query = query.Where(b => b.Extension.Equals(extTarget, StringComparison.OrdinalIgnoreCase));
            }
        }

        // 4. Reading Status filter (Default: ALL statuses active; show Reading, Plan, Completed, and None)
        bool hasStatusFilter = !string.IsNullOrWhiteSpace(SelectedStatusFilter) &&
                               !SelectedStatusFilter.Equals("ALL", StringComparison.OrdinalIgnoreCase) &&
                               !SelectedStatusFilter.Equals("Усі", StringComparison.OrdinalIgnoreCase);

        if (hasStatusFilter)
        {
            ReadingStatus targetStatus = SelectedStatusFilter switch
            {
                "Reading" => ReadingStatus.Reading,
                "Completed" => ReadingStatus.Completed,
                "Plan" => ReadingStatus.Plan,
                _ => ReadingStatus.None
            };
            query = query.Where(b => b.Status == targetStatus);
        }

        // 5. Category, Author, Tag filters
        if (!string.IsNullOrWhiteSpace(SelectedCategory) &&
            !SelectedCategory.Equals("Усі категорії", StringComparison.OrdinalIgnoreCase) &&
            !SelectedCategory.Equals("ALL", StringComparison.OrdinalIgnoreCase) &&
            !SelectedCategory.Equals("All Categories", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(b => string.Equals(b.Category, SelectedCategory, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(SelectedAuthor) &&
            !SelectedAuthor.Equals("Усі автори", StringComparison.OrdinalIgnoreCase) &&
            !SelectedAuthor.Equals("ALL", StringComparison.OrdinalIgnoreCase) &&
            !SelectedAuthor.Equals("All Authors", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(b => string.Equals(b.Author, SelectedAuthor, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(SelectedTag) &&
            !SelectedTag.Equals("Усі теги", StringComparison.OrdinalIgnoreCase) &&
            !SelectedTag.Equals("ALL", StringComparison.OrdinalIgnoreCase) &&
            !SelectedTag.Equals("All Tags", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(b => !string.IsNullOrWhiteSpace(b.Tags) &&
                                     b.Tags.Contains(SelectedTag, StringComparison.OrdinalIgnoreCase));
        }

        // 6. Search Query (Disabled if empty or contains placeholder text)
        bool hasActiveSearch = !string.IsNullOrWhiteSpace(SearchQuery) && !IsSearchPlaceholder(SearchQuery);
        if (hasActiveSearch)
        {
            string q = SearchQuery.Trim();
            if (UseFullTextFtsSearch && q.Length >= 2)
            {
                var matchedIds = await _database.SearchFtsBookIdsAsync(q);
                if (filterVersion != _filterApplicationVersion) return;
                _activeFtsMatchedIds = matchedIds;
            }
            else _activeFtsMatchedIds = null;

            query = query.Where(b =>
                (_activeFtsMatchedIds != null && _activeFtsMatchedIds.Contains(b.Id)) ||
                b.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                b.Author.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                b.Category.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                b.Tags.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                (b.Series != null && b.Series.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
                (b.Isbn != null && b.Isbn.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
                b.FilePath.Contains(q, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            _activeFtsMatchedIds = null;
        }

        // 7. Sort Order
        query = SelectedSortOrder switch
        {
            "Title" => query.OrderBy(b => b.Title),
            "Author" => query.OrderBy(b => b.Author).ThenBy(b => b.Title),
            "Pages" => query.OrderByDescending(b => b.PageCount),
            "Size" => query.OrderByDescending(b => b.FileSize),
            "Year" => query.OrderByDescending(b => b.PublishYear ?? 0),
            _ => query.OrderByDescending(b => b.LastOpenedAt ?? b.AddedAt)
        };

        var resultList = query.ToList();
        if (filterVersion != _filterApplicationVersion) return;
        var previousSelected = SelectedBook;

        _isUpdatingFilteredBooks = true;
        try
        {
            FilteredBooks.Clear();
            foreach (var item in resultList)
            {
                FilteredBooks.Add(item);
            }
        }
        finally
        {
            _isUpdatingFilteredBooks = false;
        }

        if (previousSelected != null)
        {
            SelectedBook = resultList.FirstOrDefault(b => b.Id == previousSelected.Id) ?? previousSelected;
        }

        FilteredBooksCount = FilteredBooks.Count;
        OnPropertyChanged(nameof(FilteredBooks));
        OnPropertyChanged(nameof(FilteredBooksCount));
        OnPropertyChanged(nameof(TotalBooksCount));

        if (!IsScanning)
        {
            StatusMessage = $"Показано {FilteredBooksCount} з {TotalBooksCount} книг • FTS5 індекс активний • Тема: {SelectedThemeName}";
        }
    }

    private static bool IsSearchPlaceholder(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        string t = text.Trim();
        if (t.Length == 0) return true;

        if (t.Contains("FTS5", StringComparison.OrdinalIgnoreCase) &&
            (t.Contains("запит", StringComparison.OrdinalIgnoreCase) ||
             t.Contains("query", StringComparison.OrdinalIgnoreCase) ||
             t.Contains("zapytanie", StringComparison.OrdinalIgnoreCase) ||
             t.Contains("Abfrage", StringComparison.OrdinalIgnoreCase) ||
             t.Contains("requête", StringComparison.OrdinalIgnoreCase) ||
             t.Contains("consulta", StringComparison.OrdinalIgnoreCase) ||
             t.Contains("クエリ", StringComparison.OrdinalIgnoreCase) ||
             t.Contains("查询", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if ((t.StartsWith("Пошук", StringComparison.OrdinalIgnoreCase) || t.StartsWith("Search", StringComparison.OrdinalIgnoreCase))
            && (t.EndsWith("...") || t.Contains("AND net*")))
        {
            return true;
        }

        return false;
    }

    [RelayCommand]
    private void SetFormatFilter(string? format) => SelectedFormat = string.IsNullOrWhiteSpace(format) ? "ALL" : format;

    [RelayCommand]
    private void SetStatusFilter(string? status) => SelectedStatusFilter = string.IsNullOrWhiteSpace(status) ? "ALL" : status;

    [RelayCommand]
    private void ToggleViewMode() => IsTableViewMode = !IsTableViewMode;

    [RelayCommand]
    private async Task ClearFilters()
    {
        _suppressAutoFilter = true;
        _suppressSidebarFilterSelectionChanges = true;
        try
        {
            SearchQuery = string.Empty;
            SelectedFormat = "ALL";
            SelectedStatusFilter = "ALL";
            SelectedCategory = "Усі категорії";
            SelectedAuthor = "Усі автори";
            SelectedTag = "Усі теги";
            SelectedShelf = null;
            SelectedSmartCollection = null;
            _activeShelfBookIds = null;
            _activeSmartCollection = null;
        }
        finally
        {
            _suppressSidebarFilterSelectionChanges = false;
            _suppressAutoFilter = false;
        }

        ++_sidebarFilterSelectionVersion;
        await ApplyFilterAsync();
    }

    [RelayCommand]
    private async Task CreateShelfAsync()
    {
        if (string.IsNullOrWhiteSpace(NewShelfNameInput)) return;
        await _database.CreateVirtualShelfAsync(NewShelfNameInput.Trim());
        NewShelfNameInput = string.Empty;
        await ReloadShelvesAndSmartCollectionsAsync();
    }

    [RelayCommand]
    private async Task AddSelectedBookToShelfAsync(VirtualShelfRecord? shelf)
    {
        if (SelectedBook == null || shelf == null) return;
        var book = SelectedBook;
        await _database.AssignBookToShelfAsync(book.Id, shelf.Id);

        var freshShelves = await _database.GetVirtualShelvesAsync();
        foreach (var fresh in freshShelves)
        {
            var existing = VirtualShelves.FirstOrDefault(s => s.Id == fresh.Id);
            if (existing != null) existing.BookCount = fresh.BookCount;
        }

        SelectedBook = book;
        StatusMessage = $"Книгу «{book.Title}» додано на полицю «{shelf.Name}»";
    }

    [RelayCommand]
    private async Task AddRelatedItemToBookAsync()
    {
        if (SelectedBook == null || string.IsNullOrWhiteSpace(NewRelatedTitleInput)) return;

        var item = new RelatedItemRecord
        {
            BookId = SelectedBook.Id,
            Title = NewRelatedTitleInput.Trim(),
            RelationType = NewRelatedTypeInput,
            TargetPathOrNote = NewRelatedTargetInput.Trim()
        };

        await _database.AddRelatedItemAsync(item);
        NewRelatedTitleInput = string.Empty;
        NewRelatedTargetInput = string.Empty;
        await LoadRelatedItemsForSelectedBookAsync(SelectedBook);
    }

    [RelayCommand]
    private void OpenRelatedItem(RelatedItemRecord? item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.TargetPathOrNote)) return;
        if (File.Exists(item.TargetPathOrNote) || Directory.Exists(item.TargetPathOrNote))
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = item.TargetPathOrNote, UseShellExecute = true });
            }
            catch { /* Ignore */ }
        }
        else
        {
            MessageBox.Show($"{item.Title} ({item.RelationType}):\n\n{item.TargetPathOrNote}", "Пов'язаний елемент", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    [RelayCommand]
    private async Task DeleteRelatedItemAsync(RelatedItemRecord? item)
    {
        if (item == null || SelectedBook == null) return;
        await _database.DeleteRelatedItemAsync(item.Id);
        await LoadRelatedItemsForSelectedBookAsync(SelectedBook);
    }

    [RelayCommand]
    public async Task RunFullScanAsync()
    {
        if (IsScanning) return;

        var s = _settingsService.Current;
        _scanner.ScanEbooksEnabled = s.ScanEbooksEnabled;
        _scanner.ScanDocumentsEnabled = s.ScanDocumentsEnabled;
        _scanner.EnableBackgroundFileWatcher = s.EnableBackgroundFileWatcher;
        _scanner.IsManagedLibraryEnabled = s.IsManagedLibraryEnabled;
        _scanner.ManagedLibraryMoveFiles = s.ManagedLibraryMoveFiles;
        _scanner.ManagedLibraryPath = s.GetResolvedManagedLibraryPath();

        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();

        IsScanning = true;
        StatusMessage = "Фонове сканування та індексація SQLite FTS5...";

        try
        {
            await _scanner.ScanAllFoldersAsync(_scanCts.Token);
            await ReloadLibraryFromDatabaseAsync();
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Сканування скасовано.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Помилка сканування: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
            StatusMessage = $"Показано {FilteredBooksCount} з {TotalBooksCount} книг • FTS5 індекс готовий";
        }
    }

    [RelayCommand]
    private void OpenBook(BookRecord? book)
    {
        var target = book ?? SelectedBook;
        if (target == null) return;
        OpenReaderRequested?.Invoke(target);
    }

    [RelayCommand]
    private void OpenBookExternal(BookRecord? book)
    {
        var target = book ?? SelectedBook;
        if (target == null || !File.Exists(target.FilePath)) return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = target.FilePath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Не вдалося відкрити файл у системній програмі:\n{ex.Message}", "Помилка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void OpenMetadataEditorForSelected()
    {
        if (SelectedBook == null) return;
        OpenMetadataEditorRequested?.Invoke([SelectedBook]);
    }

    public void RequestBatchMetadataEditor(List<BookRecord> selectedBooks)
    {
        if (selectedBooks.Count == 0) return;
        OpenMetadataEditorRequested?.Invoke(selectedBooks);
    }

    [RelayCommand]
    private void OpenDuplicatesDialog()
    {
        OpenDuplicatesWindowRequested?.Invoke();
    }

    [RelayCommand]
    private async Task CreateOneClickBackupAsync()
    {
        try
        {
            StatusMessage = "Створення локального резервного архіву (.zip)...";
            string zipPath = await CatalogBackupService.CreateOneClickBackupZipAsync(_database);
            StatusMessage = $"Резервну копію збережено: {zipPath}";
            MessageBox.Show($"Резервну копію бази SQLite, налаштувань, обкладинок та нотаток створено:\n\n{zipPath}",
                "Локальний бекап створено", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Помилка створення бекапу: {ex.Message}", "Помилка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task ChangeBookStatusAsync(string? statusParam)
    {
        if (SelectedBook == null || string.IsNullOrWhiteSpace(statusParam)) return;

        ReadingStatus newStatus = statusParam switch
        {
            "Reading" => ReadingStatus.Reading,
            "Completed" => ReadingStatus.Completed,
            "Plan" => ReadingStatus.Plan,
            _ => ReadingStatus.None
        };

        var book = SelectedBook;
        book.Status = newStatus;
        await _database.UpdateBookStatusAsync(book.Id, newStatus);
        SelectedBook = book;
        StatusMessage = $"Статус «{book.Title}»: {book.StatusDisplay}";
    }

    public void UpdateBookProgressFromReader(BookRecord readBook)
    {
        if (readBook == null) return;
        var target = _allBooks.FirstOrDefault(b => b.Id == readBook.Id) ?? readBook;
        target.CurrentPage = readBook.CurrentPage;
        target.PageCount = readBook.PageCount;
        target.ReadingProgress = readBook.ReadingProgress;
        target.Status = readBook.Status;
        target.LastOpenedAt = readBook.LastOpenedAt ?? DateTime.Now;

        SelectedBook = target;
        StatusMessage = $"Збережено прогрес «{target.Title}»: {target.CurrentPage}/{target.PageCount} ({target.ReadingProgress:0.0}%)";
    }

    [RelayCommand]
    private async Task SaveSelectedBookMetadataAsync()
    {
        if (SelectedBook == null) return;
        await _database.UpdateBookFullMetadataAsync(SelectedBook);
        await ReloadLibraryFromDatabaseAsync();
        StatusMessage = $"Збережено метадані для «{SelectedBook.Title}»";
    }

    [RelayCommand]
    private void OpenSettings()
    {
        OpenSettingsRequested?.Invoke();
    }

    [RelayCommand]
    private void OpenAboutWindow()
    {
        OpenAboutWindowRequested?.Invoke();
    }

    [RelayCommand]
    private void OpenWelcomeWizard()
    {
        OpenWelcomeWizardRequested?.Invoke();
    }

    private void OnScannerBookAddedOrUpdated(BookRecord book)
    {
        Application.Current?.Dispatcher.InvokeAsync(async () =>
        {
            var existingIdx = _allBooks.FindIndex(b => b.Id == book.Id || b.FilePath.Equals(book.FilePath, StringComparison.OrdinalIgnoreCase));
            if (existingIdx >= 0)
                _allBooks[existingIdx] = book;
            else
                _allBooks.Insert(0, book);

            TotalBooksCount = _allBooks.Count;
            RebuildFacetFilters();
            await ApplyFilterAsync();
        });
    }

    private void OnScannerProgressChanged(ScanProgressInfo info)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            if (!info.IsCompleted)
            {
                StatusMessage = $"Сканування та FTS5 [{info.ProcessedCount}/{info.TotalDiscovered}]: {info.CurrentFile}";
            }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanner.Dispose();
    }
}
