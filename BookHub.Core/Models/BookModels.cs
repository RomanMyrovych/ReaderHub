using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BookHub.Core.Models;

public enum ReadingStatus
{
    None = 0,
    Reading = 1,
    Completed = 2,
    Plan = 3
}

public enum ReaderViewMode
{
    SinglePage = 0,
    TwoPageSpread = 1,
    ContinuousVertical = 2
}

public class BookRecord : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void Raise(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private long _id;
    public long Id { get => _id; set => Set(ref _id, value); }

    private string _filePath = string.Empty;
    public string FilePath { get => _filePath; set => Set(ref _filePath, value); }

    private string _fileHash = string.Empty;
    public string FileHash { get => _fileHash; set => Set(ref _fileHash, value); }

    private string _title = string.Empty;
    public string Title { get => _title; set => Set(ref _title, value); }

    private string _author = "Невідомий автор";
    public string Author { get => _author; set => Set(ref _author, value); }

    private string _category = "Загальне";
    public string Category { get => _category; set => Set(ref _category, value); }

    private string _tags = string.Empty;
    public string Tags { get => _tags; set => Set(ref _tags, value); }

    private string _extension = string.Empty;
    public string Extension
    {
        get => _extension;
        set { Set(ref _extension, value); Raise(nameof(FormatBadge)); }
    }

    private long _fileSize;
    public long FileSize
    {
        get => _fileSize;
        set { Set(ref _fileSize, value); Raise(nameof(FormattedSize)); }
    }

    private int _pageCount;
    public int PageCount { get => _pageCount; set => Set(ref _pageCount, value); }

    private string? _coverPath;
    public string? CoverPath { get => _coverPath; set => Set(ref _coverPath, value); }

    private ReadingStatus _status = ReadingStatus.None;
    public ReadingStatus Status
    {
        get => _status;
        set { Set(ref _status, value); Raise(nameof(StatusDisplay)); }
    }

    private int _currentPage = 1;
    public int CurrentPage { get => _currentPage; set => Set(ref _currentPage, value); }

    private double _readingProgress;
    public double ReadingProgress { get => _readingProgress; set => Set(ref _readingProgress, value); }

    private DateTime _fileModifiedAt;
    public DateTime FileModifiedAt { get => _fileModifiedAt; set => Set(ref _fileModifiedAt, value); }

    private DateTime _addedAt = DateTime.Now;
    public DateTime AddedAt { get => _addedAt; set => Set(ref _addedAt, value); }

    private DateTime? _lastOpenedAt;
    public DateTime? LastOpenedAt { get => _lastOpenedAt; set => Set(ref _lastOpenedAt, value); }

    private string? _description;
    public string? Description { get => _description; set => Set(ref _description, value); }

    // Extended Metadata Fields
    private int? _publishYear;
    public int? PublishYear { get => _publishYear; set => Set(ref _publishYear, value); }

    private string? _isbn;
    public string? Isbn { get => _isbn; set => Set(ref _isbn, value); }

    private string? _doi;
    public string? Doi { get => _doi; set => Set(ref _doi, value); }

    private string? _publisher;
    public string? Publisher { get => _publisher; set => Set(ref _publisher, value); }

    private string? _language = "uk";
    public string? Language { get => _language; set => Set(ref _language, value); }

    private string? _series;
    public string? Series
    {
        get => _series;
        set { Set(ref _series, value); Raise(nameof(SeriesDisplay)); }
    }

    private int? _volumeNumber;
    public int? VolumeNumber
    {
        get => _volumeNumber;
        set { Set(ref _volumeNumber, value); Raise(nameof(SeriesDisplay)); }
    }

    private string? _detectedEncoding = "UTF-8";
    public string? DetectedEncoding { get => _detectedEncoding; set => Set(ref _detectedEncoding, value); }

    private int _isFtsIndexed;
    public int IsFtsIndexed { get => _isFtsIndexed; set => Set(ref _isFtsIndexed, value); }

    private long? _virtualShelfId;
    public long? VirtualShelfId { get => _virtualShelfId; set => Set(ref _virtualShelfId, value); }

    private string? _virtualShelfName;
    public string? VirtualShelfName { get => _virtualShelfName; set => Set(ref _virtualShelfName, value); }

    private long? _sectionId;
    public long? SectionId { get => _sectionId; set => Set(ref _sectionId, value); }

    private string? _sectionName;
    public string? SectionName { get => _sectionName; set => Set(ref _sectionName, value); }

    private long? _categoryId;
    public long? CategoryId { get => _categoryId; set => Set(ref _categoryId, value); }

    private string? _categoryName;
    public string? CategoryName { get => _categoryName; set => Set(ref _categoryName, value); }

    private long? _shelfId;
    public long? ShelfId { get => _shelfId; set => Set(ref _shelfId, value); }

    private string? _shelfName;
    public string? ShelfName { get => _shelfName; set => Set(ref _shelfName, value); }

    private bool _isSelectedForEdit;
    public bool IsSelectedForEdit { get => _isSelectedForEdit; set => Set(ref _isSelectedForEdit, value); }

    public string FormattedSize => FormatBytes(FileSize);
    public string FormatBadge => Extension.TrimStart('.').ToUpperInvariant();
    public string StatusDisplay => Status switch
    {
        ReadingStatus.Reading => "📖 Читаю",
        ReadingStatus.Completed => "✅ Прочитано",
        ReadingStatus.Plan => "📌 План",
        _ => "📚 У бібліотеці"
    };

    public string SeriesDisplay => !string.IsNullOrWhiteSpace(Series)
        ? (VolumeNumber.HasValue ? $"{Series} (#{VolumeNumber})" : Series)
        : string.Empty;

    private static string FormatBytes(long bytes)
    {
        string[] suffixes = ["Б", "КБ", "МБ", "ГБ"];
        int counter = 0;
        double number = bytes;
        while (Math.Round(number / 1024) >= 1 && counter < suffixes.Length - 1)
        {
            number /= 1024;
            counter++;
        }
        return $"{number:0.#} {suffixes[counter]}";
    }
}

public class AnnotationRecord
{
    public long Id { get; set; }
    public long BookId { get; set; }
    public int PageNumber { get; set; } = 1;
    public double ScrollOffset { get; set; }
    public string QuoteText { get; set; } = string.Empty;
    public string CommentText { get; set; } = string.Empty;
    public string HighlightColor { get; set; } = "#FACC15";
    public bool IsBookmark { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    [System.Text.Json.Serialization.JsonIgnore]
    public string StickyBgColorHex
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(HighlightColor) && HighlightColor.StartsWith('#'))
            {
                var hex = HighlightColor.TrimStart('#');
                if (hex.Length == 6) return $"#40{hex}";
                if (hex.Length == 8) return $"#40{hex[2..]}";
            }
            return "#4038BDF8";
        }
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public string StickyBorderColorHex => string.IsNullOrWhiteSpace(HighlightColor) ? "#38BDF8" : HighlightColor;
}

public class TocItem
{
    public string Title { get; set; } = string.Empty;
    public int PageNumber { get; set; }
    public string? ContentKey { get; set; }
    public List<TocItem> Children { get; set; } = [];
}

public class ParsedBookMetadata
{
    public string Title { get; set; } = string.Empty;
    public string Author { get; set; } = "Невідомий автор";
    public string Category { get; set; } = "Загальне";
    public string Tags { get; set; } = string.Empty;
    public int PageCount { get; set; }
    public string? Description { get; set; }
    public byte[]? CoverImageBytes { get; set; }
    public int? PublishYear { get; set; }
    public string? Isbn { get; set; }
    public string? Doi { get; set; }
    public string? Publisher { get; set; }
    public string? Language { get; set; } = "uk";
    public string? Series { get; set; }
    public int? VolumeNumber { get; set; }
    public string? DetectedEncoding { get; set; } = "UTF-8";
}

public class LibrarySection
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Icon { get; set; } = "📁";
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public int BookCount { get; set; }
    public List<LibraryCategory> Categories { get; set; } = [];
}

public class LibraryCategory
{
    public long Id { get; set; }
    public long SectionId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Icon { get; set; } = "📂";
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public int BookCount { get; set; }
    public List<LibraryShelf> Shelves { get; set; } = [];
}

public class LibraryShelf
{
    public long Id { get; set; }
    public long CategoryId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Icon { get; set; } = "📖";
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public int BookCount { get; set; }
}
