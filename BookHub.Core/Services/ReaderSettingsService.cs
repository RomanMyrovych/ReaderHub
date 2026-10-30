using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BookHub.Core.Models;

namespace BookHub.Core.Services;

public sealed class RecentReaderFileEntry
{
    public string FilePath { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Author { get; set; } = "Невідомий автор";
    public string Extension { get; set; } = ".pdf";
    public int CurrentPage { get; set; } = 1;
    public int TotalPages { get; set; } = 1;
    public double ReadingProgress { get; set; }
    public DateTime LastOpenedAt { get; set; } = DateTime.Now;
    public List<AnnotationRecord> LocalAnnotations { get; set; } = [];

    [JsonIgnore]
    public string FileName => string.IsNullOrWhiteSpace(FilePath)
        ? Title
        : Path.GetFileName(FilePath);

    [JsonIgnore]
    public bool FileExists => !string.IsNullOrWhiteSpace(FilePath) && File.Exists(FilePath);

    [JsonIgnore]
    public string FormatBadge => Extension.TrimStart('.').ToUpperInvariant();

    [JsonIgnore]
    public string ProgressSummary => TotalPages > 0
        ? $"Стор. {CurrentPage} / {TotalPages} ({ReadingProgress:0.0}%)"
        : $"Стор. {CurrentPage}";

    [JsonIgnore]
    public string LastOpenedDisplay => LastOpenedAt.ToString("dd.MM.yyyy HH:mm");
}

public sealed class ReaderSessionTabEntry
{
    public string FilePath { get; set; } = string.Empty;
    public int CurrentPage { get; set; } = 1;
    public double SavedScrollOffset { get; set; }
    public double SavedPageRelativeOffset { get; set; }
    public string ZoomMode { get; set; } = "FitWidth";
    public string ViewMode { get; set; } = "ContinuousVertical";
    public bool IsPinned { get; set; }
}

public sealed class ReaderStandaloneSettings
{
    // Window geometry
    public double WindowWidth { get; set; } = 1360;
    public double WindowHeight { get; set; } = 880;
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public string WindowState { get; set; } = "Maximized";

    // Splitters & drawers
    public double TocWidth { get; set; } = 285;
    public double NotesWidth { get; set; } = 320;
    public bool IsTocVisible { get; set; } = true;
    public bool IsAnnotationsVisible { get; set; } = false;

    // View, Theme, Localization & First-Run License
    public string ZoomMode { get; set; } = "FitWidth";
    public string LastReaderViewMode { get; set; } = "ContinuousVertical";
    public string ThemeName { get; set; } = "Obsidian Dark";
    public string Language { get; set; } = "uk";
    public bool IsLicenseAccepted { get; set; } = false;

    // Smart PDF Dark/Sepia Re-Coloring & Session Restore
    public bool IsSmartPdfColorFilterEnabled { get; set; } = true;
    public bool RestorePreviousSessionOnStartup { get; set; } = true;
    public string ActiveSessionFilePath { get; set; } = string.Empty;
    public List<ReaderSessionTabEntry> OpenSessionTabs { get; set; } = [];

    public string Theme
    {
        get => ThemeName;
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
                ThemeName = value;
        }
    }

    // Top 10 recent files for standalone start screen & local progress persistence
    public List<RecentReaderFileEntry> RecentFiles { get; set; } = [];
}

public sealed class ReaderSettingsService
{
    public const int MaxRecentFiles = 10;
    public const string DefaultSettingsFileName = "allreaderbook_settings.json";
    public const string LegacySettingsFileName = "reader_settings.json";

    private readonly string _settingsFilePath;
    private readonly string _legacySettingsFilePath;
    private readonly object _lock = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    public ReaderStandaloneSettings Current { get; private set; } = new();
    public string SettingsFilePath => _settingsFilePath;
    public event Action? RecentFilesChanged;

    public ReaderSettingsService(string? rootDirectory = null)
    {
        string baseDir = rootDirectory ?? AppDomain.CurrentDomain.BaseDirectory;
        Directory.CreateDirectory(baseDir);
        _settingsFilePath = Path.Combine(baseDir, DefaultSettingsFileName);
        _legacySettingsFilePath = Path.Combine(baseDir, LegacySettingsFileName);
        Load();
    }

    public ReaderStandaloneSettings Load()
    {
        lock (_lock)
        {
            string sourcePath = _settingsFilePath;
            bool migratedFromLegacy = false;

            if (!File.Exists(_settingsFilePath))
            {
                if (File.Exists(_legacySettingsFilePath))
                {
                    sourcePath = _legacySettingsFilePath;
                    migratedFromLegacy = true;
                }
                else
                {
                    Current = new ReaderStandaloneSettings();
                    Save();
                    return Current;
                }
            }

            try
            {
                string json = File.ReadAllText(sourcePath, Encoding.UTF8);
                var loaded = JsonSerializer.Deserialize<ReaderStandaloneSettings>(json, JsonOptions);
                Current = loaded ?? new ReaderStandaloneSettings();
                Current.RecentFiles ??= [];
                Current.OpenSessionTabs ??= [];
                if (migratedFromLegacy)
                {
                    Save();
                }
            }
            catch
            {
                Current = new ReaderStandaloneSettings();
            }

            return Current;
        }
    }

    public void Save()
    {
        lock (_lock)
        {
            try
            {
                string json = JsonSerializer.Serialize(Current, JsonOptions);
                File.WriteAllText(_settingsFilePath, json, Encoding.UTF8);
            }
            catch
            {
                // Ignore transient file access errors
            }
        }
    }

    public RecentReaderFileEntry? GetRecentEntry(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;
        string normalized = NormalizePath(filePath);
        lock (_lock)
        {
            return Current.RecentFiles.FirstOrDefault(r =>
                string.Equals(NormalizePath(r.FilePath), normalized, StringComparison.OrdinalIgnoreCase));
        }
    }

    public void UpsertRecentFile(BookRecord book, IEnumerable<AnnotationRecord>? annotations = null)
    {
        if (string.IsNullOrWhiteSpace(book.FilePath)) return;
        string normalized = NormalizePath(book.FilePath);

        lock (_lock)
        {
            var existing = Current.RecentFiles.FirstOrDefault(r =>
                string.Equals(NormalizePath(r.FilePath), normalized, StringComparison.OrdinalIgnoreCase));

            var preservedAnnotations = annotations?.ToList() ?? existing?.LocalAnnotations ?? [];
            if (existing != null)
            {
                Current.RecentFiles.Remove(existing);
            }

            int total = Math.Max(1, book.PageCount);
            int cur = Math.Clamp(book.CurrentPage, 1, total);
            double progress = Math.Clamp(Math.Round((double)cur / total * 100.0, 1), 0, 100);

            var entry = new RecentReaderFileEntry
            {
                FilePath = normalized,
                Title = string.IsNullOrWhiteSpace(book.Title)
                    ? Path.GetFileNameWithoutExtension(normalized)
                    : book.Title,
                Author = string.IsNullOrWhiteSpace(book.Author) ? "Невідомий автор" : book.Author,
                Extension = string.IsNullOrWhiteSpace(book.Extension)
                    ? Path.GetExtension(normalized).ToLowerInvariant()
                    : book.Extension,
                CurrentPage = cur,
                TotalPages = total,
                ReadingProgress = progress,
                LastOpenedAt = DateTime.Now,
                LocalAnnotations = preservedAnnotations
            };

            Current.RecentFiles.Insert(0, entry);
            if (Current.RecentFiles.Count > MaxRecentFiles)
            {
                Current.RecentFiles.RemoveRange(MaxRecentFiles, Current.RecentFiles.Count - MaxRecentFiles);
            }
        }

        Save();
        RecentFilesChanged?.Invoke();
    }

    public void RemoveRecentFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;
        string normalized = NormalizePath(filePath);
        lock (_lock)
        {
            Current.RecentFiles.RemoveAll(r =>
                string.Equals(NormalizePath(r.FilePath), normalized, StringComparison.OrdinalIgnoreCase));
        }
        Save();
        RecentFilesChanged?.Invoke();
    }

    public void ClearRecentFiles()
    {
        lock (_lock)
        {
            Current.RecentFiles.Clear();
        }
        Save();
        RecentFilesChanged?.Invoke();
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path.Trim('"', ' '));
        }
        catch
        {
            return path.Trim('"', ' ');
        }
    }
}
