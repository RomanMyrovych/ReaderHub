using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using BookHub.App.Services;
using BookHub.Core.Data;
using BookHub.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace BookHub.App.ViewModels;

public sealed partial class WizardLanguageCard : ObservableObject
{
    public string Code { get; }
    public string FlagEmoji { get; }
    public string NativeTitle { get; }
    public string Subtitle { get; }
    [ObservableProperty] private bool _isSelected;

    public WizardLanguageCard(string code, string flagEmoji, string nativeTitle, string subtitle, bool isSelected = false)
    {
        Code = code;
        FlagEmoji = flagEmoji;
        NativeTitle = nativeTitle;
        Subtitle = subtitle;
        _isSelected = isSelected;
    }
}

public sealed partial class WizardThemeCard : ObservableObject
{
    public string ThemeName { get; }
    public string DisplayTitle { get; }
    public string Subtitle { get; }
    public Brush BgPreviewBrush { get; }
    public Brush CardPreviewBrush { get; }
    public Brush AccentPreviewBrush { get; }
    public Brush TextPreviewBrush { get; }
    [ObservableProperty] private bool _isSelected;

    public WizardThemeCard(
        string themeName,
        string displayTitle,
        string subtitle,
        Brush bgPreviewBrush,
        Brush cardPreviewBrush,
        Brush accentPreviewBrush,
        Brush textPreviewBrush,
        bool isSelected = false)
    {
        ThemeName = themeName;
        DisplayTitle = displayTitle;
        Subtitle = subtitle;
        BgPreviewBrush = bgPreviewBrush;
        CardPreviewBrush = cardPreviewBrush;
        AccentPreviewBrush = accentPreviewBrush;
        TextPreviewBrush = textPreviewBrush;
        _isSelected = isSelected;
    }
}

public partial class WelcomeWizardViewModel : ObservableObject
{
    private readonly LibraryDatabase _database;
    private readonly UserSettingsService _settingsService;

    public IReadOnlyList<WizardLanguageCard> LanguageCards { get; } =
    [
        new("uk", "🇺🇦", "Українська", "Ukrainian"),
        new("en", "🇬🇧", "English", "English"),
        new("pl", "🇵🇱", "Polski", "Polish"),
        new("de", "🇩🇪", "Deutsch", "German"),
        new("fr", "🇫🇷", "Français", "French"),
        new("es", "🇪🇸", "Español", "Spanish"),
        new("it", "🇮🇹", "Italiano", "Italian"),
        new("pt", "🇵🇹", "Português", "Portuguese"),
        new("ja", "🇯🇵", "日本語", "Japanese"),
        new("zh", "🇨🇳", "简体中文", "Chinese")
    ];

    public IReadOnlyList<WizardThemeCard> FeaturedThemeCards { get; } =
    [
        new(
            "OLED Black",
            "OLED Black",
            "Pitch Black #000000 • Максимальний контраст",
            new SolidColorBrush(Color.FromRgb(0x00, 0x00, 0x00)),
            new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x16)),
            new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)),
            new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF7))),
        new(
            "Obsidian Dark",
            "Obsidian Dark",
            "Графітовий темний стиль • За замовчуванням",
            new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x18)),
            new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x2B)),
            new SolidColorBrush(Color.FromRgb(0x8B, 0x5C, 0xF6)),
            new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xDD))),
        new(
            "Paper Sepia Classic",
            "Warm Sepia",
            "Теплий паперовий тон • Для тривалого читання",
            new SolidColorBrush(Color.FromRgb(0xF4, 0xEC, 0xD8)),
            new SolidColorBrush(Color.FromRgb(0xFB, 0xF6, 0xE9)),
            new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09)),
            new SolidColorBrush(Color.FromRgb(0x3B, 0x2F, 0x2F))),
        new(
            "Clean White",
            "Clean White",
            "Чистий білий день • Класична типографіка",
            new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)),
            new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC)),
            new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)),
            new SolidColorBrush(Color.FromRgb(0x0F, 0x17, 0x2A)))
    ];

    public IReadOnlyList<string> AllThemeNames { get; } = ThemeManagerService.Presets.Select(p => p.Name).ToList();

    public ObservableCollection<string> SelectedFolders { get; } = [];

    [ObservableProperty] private int _currentStep = 1;
    [ObservableProperty] private string _selectedLanguageCode = "uk";
    [ObservableProperty] private string _selectedThemeName = "Obsidian Dark";
    [ObservableProperty] private bool _isTableViewMode;
    [ObservableProperty] private bool _scanEbooksEnabled = true;
    [ObservableProperty] private bool _scanDocumentsEnabled = true;
    [ObservableProperty] private bool _enableBackgroundFileWatcher = true;
    [ObservableProperty] private bool _isPortableStorageMode = true;

    public bool IsStep1 => CurrentStep == 1;
    public bool IsStep2 => CurrentStep == 2;
    public bool IsStep3 => CurrentStep == 3;
    public bool IsStep4 => CurrentStep == 4;
    public bool IsStep5 => CurrentStep == 5;

    public bool CanGoBack => CurrentStep > 1;
    public bool CanGoNext => CurrentStep < 5;
    public bool IsFinalStep => CurrentStep == 5;

    public bool IsGridViewMode
    {
        get => !IsTableViewMode;
        set => IsTableViewMode = !value;
    }

    public bool IsStandardStorageMode
    {
        get => !IsPortableStorageMode;
        set => IsPortableStorageMode = !value;
    }

    public string ActiveDatabasePathPreview => _database.DatabasePath;
    public string ActiveCachePathPreview => _database.CacheDirectory;

    public string SummaryLanguageText => LanguageCards.FirstOrDefault(l => l.Code == SelectedLanguageCode)?.NativeTitle ?? SelectedLanguageCode;
    public string SummaryThemeText => SelectedThemeName;
    public string SummaryDensityText => IsTableViewMode ? "☰ Компактна таблиця (Table)" : "▦ Сітка обкладинок (Grid)";
    public string SummaryFoldersText => SelectedFolders.Count > 0
        ? $"{SelectedFolders.Count} папок: {string.Join("; ", SelectedFolders)}"
        : "Папки з книгами не вибрані";
    public string SummaryFormatsText
    {
        get
        {
            var parts = new List<string>();
            if (ScanEbooksEnabled) parts.Add("Електронні книги (PDF/EPUB/FB2/MOBI/DJVU/CBZ)");
            if (ScanDocumentsEnabled) parts.Add("Документи та код (DOCX/MD/TXT/HTML/RTF/ODT)");
            if (parts.Count == 0) parts.Add("Усі формати за замовчуванням");
            return string.Join(" + ", parts);
        }
    }
    public string SummaryStorageModeText => IsPortableStorageMode
        ? $"Локальне зберігання у папці програми ({Path.GetDirectoryName(_database.DatabasePath)})"
        : "Стандартний профіль користувача (з локальним захистом системного диска)";

    public event Action<bool>? WizardFinished;

    public WelcomeWizardViewModel(LibraryDatabase database, UserSettingsService settingsService)
    {
        _database = database;
        _settingsService = settingsService;

        var s = _settingsService.Current;
        _selectedLanguageCode = string.IsNullOrWhiteSpace(s.LanguageCode) ? "uk" : s.LanguageCode;
        _selectedThemeName = string.IsNullOrWhiteSpace(s.ThemeName) ? "Obsidian Dark" : s.ThemeName;
        _isTableViewMode = s.IsTableViewMode;
        _scanEbooksEnabled = s.ScanEbooksEnabled;
        _scanDocumentsEnabled = s.ScanDocumentsEnabled;
        _enableBackgroundFileWatcher = s.EnableBackgroundFileWatcher;
        _isPortableStorageMode = s.IsPortableStorageMode;

        LocalizationService.ApplyLanguage(_selectedLanguageCode);
        ThemeManagerService.ApplyTheme(_selectedThemeName);
        UpdateLanguageCardsSelection();
        UpdateThemeCardsSelection();
    }

    private void UpdateLanguageCardsSelection()
    {
        foreach (var card in LanguageCards)
        {
            card.IsSelected = string.Equals(card.Code, SelectedLanguageCode, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void UpdateThemeCardsSelection()
    {
        foreach (var card in FeaturedThemeCards)
        {
            card.IsSelected = string.Equals(card.ThemeName, SelectedThemeName, StringComparison.OrdinalIgnoreCase);
        }
    }

    public async Task LoadInitialFoldersAsync()
    {
        var existing = await _database.GetRootFoldersAsync();
        SelectedFolders.Clear();
        foreach (var f in existing)
        {
            SelectedFolders.Add(f.Path);
        }

        RefreshSummaryProperties();
    }

    partial void OnCurrentStepChanged(int value)
    {
        OnPropertyChanged(nameof(IsStep1));
        OnPropertyChanged(nameof(IsStep2));
        OnPropertyChanged(nameof(IsStep3));
        OnPropertyChanged(nameof(IsStep4));
        OnPropertyChanged(nameof(IsStep5));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(IsFinalStep));
        RefreshSummaryProperties();
    }

    partial void OnSelectedLanguageCodeChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            LocalizationService.ApplyLanguage(value);
            UpdateLanguageCardsSelection();
            RefreshSummaryProperties();
        }
    }

    partial void OnSelectedThemeNameChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            ThemeManagerService.ApplyTheme(value);
            UpdateThemeCardsSelection();
            RefreshSummaryProperties();
        }
    }

    partial void OnIsTableViewModeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsGridViewMode));
        RefreshSummaryProperties();
    }

    partial void OnIsPortableStorageModeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsStandardStorageMode));
        RefreshSummaryProperties();
    }

    private void RefreshSummaryProperties()
    {
        OnPropertyChanged(nameof(SummaryLanguageText));
        OnPropertyChanged(nameof(SummaryThemeText));
        OnPropertyChanged(nameof(SummaryDensityText));
        OnPropertyChanged(nameof(SummaryFoldersText));
        OnPropertyChanged(nameof(SummaryFormatsText));
        OnPropertyChanged(nameof(SummaryStorageModeText));
    }

    [RelayCommand]
    private void SelectLanguage(string langCode)
    {
        if (!string.IsNullOrWhiteSpace(langCode))
        {
            SelectedLanguageCode = langCode;
        }
    }

    [RelayCommand]
    private void SelectTheme(string themeName)
    {
        if (!string.IsNullOrWhiteSpace(themeName))
        {
            SelectedThemeName = themeName;
        }
    }

    [RelayCommand]
    private void SelectDensityMode(string mode)
    {
        IsTableViewMode = string.Equals(mode, "Table", StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    private void SelectStorageMode(string mode)
    {
        IsPortableStorageMode = !string.Equals(mode, "Standard", StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    private void GoToStep(string stepParam)
    {
        if (int.TryParse(stepParam, out int target) && target >= 1 && target <= 5)
        {
            CurrentStep = target;
        }
    }

    [RelayCommand]
    private void NextStep()
    {
        if (CurrentStep < 5)
            CurrentStep++;
    }

    [RelayCommand]
    private void PreviousStep()
    {
        if (CurrentStep > 1)
            CurrentStep--;
    }

    [RelayCommand]
    private void BrowseAndAddFolder()
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Оберіть папку з книгами або документами",
            Multiselect = true
        };

        if (dlg.ShowDialog() == true)
        {
            foreach (var folder in dlg.FolderNames)
            {
                AddFolderPathIfValid(folder);
            }
        }
    }

    public void AddFolderPathIfValid(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            return;

        string normalized = Path.GetFullPath(folderPath.Trim());
        if (!SelectedFolders.Any(f => string.Equals(f, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            SelectedFolders.Add(normalized);
            RefreshSummaryProperties();
        }
    }

    [RelayCommand]
    private void RemoveFolder(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return;
        SelectedFolders.Remove(folderPath);
        RefreshSummaryProperties();
    }

    [RelayCommand]
    private async Task SkipWizardAsync()
    {
        await PersistSettingsAndFoldersAsync();
        WizardFinished?.Invoke(false);
    }

    [RelayCommand]
    private async Task FinishWizardAsync()
    {
        await PersistSettingsAndFoldersAsync();
        WizardFinished?.Invoke(true);
    }

    private async Task PersistSettingsAndFoldersAsync()
    {
        if (!ScanEbooksEnabled && !ScanDocumentsEnabled)
        {
            ScanEbooksEnabled = true;
            ScanDocumentsEnabled = true;
        }

        var s = _settingsService.Current;
        s.IsFirstRun = false;
        s.IsFirstLaunch = false;
        s.LanguageCode = SelectedLanguageCode;
        s.ThemeName = SelectedThemeName;
        s.IsTableViewMode = IsTableViewMode;
        s.ScanEbooksEnabled = ScanEbooksEnabled;
        s.ScanDocumentsEnabled = ScanDocumentsEnabled;
        s.EnableBackgroundFileWatcher = EnableBackgroundFileWatcher;
        s.IsPortableStorageMode = IsPortableStorageMode;
        _settingsService.Save();

        await _database.InitializeAsync();
        var currentDbFolders = await _database.GetRootFoldersAsync();

        // Remove folders deselected in wizard
        foreach (var existing in currentDbFolders)
        {
            if (!SelectedFolders.Any(f => string.Equals(f, existing.Path, StringComparison.OrdinalIgnoreCase)))
            {
                await _database.RemoveRootFolderAsync(existing.Id, existing.Path);
            }
        }

        // Add all folders configured in wizard
        foreach (var folder in SelectedFolders)
        {
            if (Directory.Exists(folder))
            {
                await _database.AddRootFolderAsync(folder);
            }
        }
    }
}
