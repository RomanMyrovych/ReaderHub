namespace BookHub.Core.Models;

public class RootFolderRecord
{
    public long Id { get; set; }
    public string Path { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public DateTime AddedAt { get; set; } = DateTime.Now;
    public DateTime? LastScannedAt { get; set; }
}

public class VirtualShelfRecord
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Icon { get; set; } = "📁";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public int BookCount { get; set; }
}

public class SmartCollectionRecord
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? KeywordRule { get; set; }
    public string? FormatRule { get; set; }
    public int? StatusRule { get; set; }
    public int? AddedThisYearOnly { get; set; }
    public string Icon { get; set; } = "⚡";
}

public class RelatedItemRecord
{
    public long Id { get; set; }
    public long BookId { get; set; }
    public long? RelatedBookId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string RelationType { get; set; } = "Конспект";
    public string TargetPathOrNote { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

public class DuplicateGroup
{
    public string GroupKey { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public List<BookRecord> Books { get; set; } = [];
}

public class UserSettings
{
    public bool IsFirstRun { get; set; } = true;
    public bool IsFirstLaunch { get; set; } = true;

    // Main Window geometry
    public double MainWindowWidth { get; set; } = 1480;
    public double MainWindowHeight { get; set; } = 900;
    public double MainWindowLeft { get; set; } = double.NaN;
    public double MainWindowTop { get; set; } = double.NaN;
    public string MainWindowState { get; set; } = "Maximized";

    // Reader Window geometry
    public double ReaderWindowWidth { get; set; } = 1360;
    public double ReaderWindowHeight { get; set; } = 880;
    public double ReaderWindowLeft { get; set; } = double.NaN;
    public double ReaderWindowTop { get; set; } = double.NaN;
    public string ReaderWindowState { get; set; } = "Maximized";

    // GridSplitter widths
    public double LeftSidebarWidth { get; set; } = 280;
    public double RightInspectorWidth { get; set; } = 330;
    public double ReaderTocWidth { get; set; } = 285;
    public double ReaderNotesWidth { get; set; } = 320;

    // Theme, Localization, Zoom & View state
    public string LanguageCode { get; set; } = "uk";
    public string ReaderZoomMode { get; set; } = "FitWidth";
    public string LastReaderViewMode { get; set; } = "ContinuousVertical";
    public string ThemeName { get; set; } = "Obsidian Dark";
    public bool IsTableViewMode { get; set; } = false;
    public string SelectedFormat { get; set; } = "ALL";
    public string SelectedStatusFilter { get; set; } = "ALL";
    public string SelectedCategory { get; set; } = "Усі категорії";
    public string SelectedAuthor { get; set; } = "Усі автори";
    public string SelectedTag { get; set; } = "Усі теги";
    public string SortOrder { get; set; } = "Recent";
    public bool EnableOcrFallbackForScans { get; set; } = true;

    // First-Run Wizard & Scanner preferences
    public bool ScanEbooksEnabled { get; set; } = true;
    public bool ScanDocumentsEnabled { get; set; } = true;
    public bool EnableBackgroundFileWatcher { get; set; } = true;
    public bool IsPortableStorageMode { get; set; } = true;

    // Reader enhancements (ported from AllReaderBook)
    public bool IsSmartPdfColorFilterEnabled { get; set; } = true;
    public List<string> RecentReaderFiles { get; set; } = [];

    // Managed Library (\libofbooks) settings
    public bool IsManagedLibraryEnabled { get; set; } = false;
    public bool ManagedLibraryMoveFiles { get; set; } = false;
    public string ManagedLibraryCustomPath { get; set; } = string.Empty;

    public string GetResolvedManagedLibraryPath(string? defaultBaseDirectory = null)
    {
        string baseDir = string.IsNullOrWhiteSpace(defaultBaseDirectory)
            ? AppDomain.CurrentDomain.BaseDirectory
            : defaultBaseDirectory;

        if (!string.IsNullOrWhiteSpace(ManagedLibraryCustomPath))
        {
            string trimmed = ManagedLibraryCustomPath.Trim().TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            string leafName = System.IO.Path.GetFileName(trimmed);
            if (string.Equals(leafName, "libofbooks", StringComparison.OrdinalIgnoreCase))
                return trimmed;
            return System.IO.Path.Combine(trimmed, "libofbooks");
        }

        return System.IO.Path.Combine(
            baseDir.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar),
            "libofbooks");
    }
}
