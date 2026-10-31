using Dapper;
using Microsoft.Data.Sqlite;
using BookHub.Core.Models;

namespace BookHub.Core.Data;

public class LibraryDatabase
{
    private readonly string _connectionString;
    public string DatabasePath { get; }
    public string CacheDirectory { get; }

    /// <summary>
    /// Directory that hosts the running executable. All runtime data (bookhub.db, cache/,
    /// settings.json, Backups/) lives here so installed and portable builds stay self-contained,
    /// even when launched from a Desktop shortcut whose Working Directory is C:\Windows\System32.
    /// </summary>
    public static string DataDirectory => AppDomain.CurrentDomain.BaseDirectory;

    public LibraryDatabase(string? baseDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(baseDirectory) &&
            (baseDirectory.EndsWith(".db", StringComparison.OrdinalIgnoreCase) ||
             baseDirectory.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase) ||
             File.Exists(baseDirectory)))
        {
            string fullDbPath = Path.GetFullPath(baseDirectory);
            string rootDir = Path.GetDirectoryName(fullDbPath) ?? DataDirectory;
            Directory.CreateDirectory(rootDir);
            DatabasePath = fullDbPath;
            CacheDirectory = Path.Combine(rootDir, "cache");
            Directory.CreateDirectory(CacheDirectory);
        }
        else
        {
            var rootDir = baseDirectory ?? DataDirectory;
            Directory.CreateDirectory(rootDir);

            DatabasePath = Path.Combine(rootDir, "bookhub.db");
            CacheDirectory = Path.Combine(rootDir, "cache");
            Directory.CreateDirectory(CacheDirectory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    /// <summary>
    /// Resolves a read-only directory that ships with the app (e.g. SampleLibrary). Prefers the
    /// folder next to the executable and only falls back to the repository root during development.
    /// </summary>
    public static string ResolveResourceDirectory(string directoryName)
    {
        var local = Path.Combine(DataDirectory, directoryName);
        if (Directory.Exists(local)) return local;
        return Path.Combine(ResolveProjectRoot(), directoryName);
    }

    /// <summary>
    /// Resolves a read-only file that ships with the app (e.g. LICENSE, THIRD_PARTY_LICENSES.txt).
    /// Prefers the folder next to the executable and only falls back to the repository root during development.
    /// </summary>
    public static string ResolveResourceFile(string fileName)
    {
        var local = Path.Combine(DataDirectory, fileName);
        if (File.Exists(local)) return local;
        return Path.Combine(ResolveProjectRoot(), fileName);
    }

    /// <summary>
    /// Development-only helper: locates the source repository root. Never used for runtime data paths.
    /// </summary>
    public static string ResolveProjectRoot()
    {
        if (HasProjectMarker(DataDirectory)) return DataDirectory;

        var dir = new DirectoryInfo(DataDirectory);
        while (dir != null)
        {
            if (HasProjectMarker(dir.FullName))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return DataDirectory;
    }

    private static bool HasProjectMarker(string directory) =>
        File.Exists(Path.Combine(directory, "BookHub.sln")) ||
        File.Exists(Path.Combine(directory, "nuget.config"));

    private SqliteConnection CreateConnection() => new(_connectionString);

    public async Task InitializeAsync()
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync();

        var sql = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;

            CREATE TABLE IF NOT EXISTS RootFolders (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Path TEXT NOT NULL UNIQUE,
                IsEnabled INTEGER NOT NULL DEFAULT 1,
                AddedAt TEXT NOT NULL,
                LastScannedAt TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS Books (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                FilePath TEXT NOT NULL UNIQUE,
                FileHash TEXT NOT NULL,
                Title TEXT NOT NULL,
                Author TEXT NOT NULL,
                Category TEXT NOT NULL DEFAULT 'Загальне',
                Tags TEXT NOT NULL DEFAULT '',
                Extension TEXT NOT NULL,
                FileSize INTEGER NOT NULL,
                PageCount INTEGER NOT NULL DEFAULT 0,
                CoverPath TEXT NULL,
                Status INTEGER NOT NULL DEFAULT 0,
                CurrentPage INTEGER NOT NULL DEFAULT 1,
                ReadingProgress REAL NOT NULL DEFAULT 0,
                FileModifiedAt TEXT NOT NULL,
                AddedAt TEXT NOT NULL,
                LastOpenedAt TEXT NULL,
                Description TEXT NULL,
                PublishYear INTEGER NULL,
                Isbn TEXT NULL,
                Doi TEXT NULL,
                Publisher TEXT NULL,
                Language TEXT NULL DEFAULT 'uk',
                Series TEXT NULL,
                VolumeNumber INTEGER NULL,
                DetectedEncoding TEXT NULL DEFAULT 'UTF-8',
                IsFtsIndexed INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS IX_Books_FileHash ON Books(FileHash);
            CREATE INDEX IF NOT EXISTS IX_Books_Extension ON Books(Extension);
            CREATE INDEX IF NOT EXISTS IX_Books_Status ON Books(Status);
            CREATE INDEX IF NOT EXISTS IX_Books_Author ON Books(Author);
            CREATE INDEX IF NOT EXISTS IX_Books_Category ON Books(Category);

            CREATE VIRTUAL TABLE IF NOT EXISTS BooksFts USING fts5(
                BookId UNINDEXED,
                Title,
                Author,
                Tags,
                Content,
                tokenize='unicode61'
            );

            CREATE TABLE IF NOT EXISTS VirtualShelves (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL UNIQUE,
                Icon TEXT NOT NULL DEFAULT '📁',
                CreatedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS BookShelves (
                BookId INTEGER NOT NULL,
                ShelfId INTEGER NOT NULL,
                PRIMARY KEY (BookId, ShelfId)
            );

            CREATE TABLE IF NOT EXISTS SmartCollections (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL UNIQUE,
                KeywordRule TEXT NULL,
                FormatRule TEXT NULL,
                StatusRule INTEGER NULL,
                AddedThisYearOnly INTEGER NULL,
                Icon TEXT NOT NULL DEFAULT '⚡'
            );

            CREATE TABLE IF NOT EXISTS RelatedItems (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                BookId INTEGER NOT NULL,
                RelatedBookId INTEGER NULL,
                Title TEXT NOT NULL,
                RelationType TEXT NOT NULL,
                TargetPathOrNote TEXT NOT NULL,
                CreatedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS Annotations (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                BookId INTEGER NOT NULL,
                PageNumber INTEGER NOT NULL DEFAULT 1,
                ScrollOffset REAL NOT NULL DEFAULT 0,
                QuoteText TEXT NOT NULL DEFAULT '',
                CommentText TEXT NOT NULL DEFAULT '',
                HighlightColor TEXT NOT NULL DEFAULT '#FACC15',
                IsBookmark INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT NOT NULL
            );
            """;

        await conn.ExecuteAsync(sql);

        // Migrate existing Books table if columns were created in an earlier schema version
        string[] alterColumns =
        [
            "ALTER TABLE Books ADD COLUMN PublishYear INTEGER NULL;",
            "ALTER TABLE Books ADD COLUMN Isbn TEXT NULL;",
            "ALTER TABLE Books ADD COLUMN Doi TEXT NULL;",
            "ALTER TABLE Books ADD COLUMN Publisher TEXT NULL;",
            "ALTER TABLE Books ADD COLUMN Language TEXT NULL DEFAULT 'uk';",
            "ALTER TABLE Books ADD COLUMN Series TEXT NULL;",
            "ALTER TABLE Books ADD COLUMN VolumeNumber INTEGER NULL;",
            "ALTER TABLE Books ADD COLUMN DetectedEncoding TEXT NULL DEFAULT 'UTF-8';",
            "ALTER TABLE Books ADD COLUMN IsFtsIndexed INTEGER NOT NULL DEFAULT 0;",
            "ALTER TABLE Annotations ADD COLUMN ScrollOffset REAL NOT NULL DEFAULT 0;"
        ];

        foreach (var alter in alterColumns)
        {
            try { await conn.ExecuteAsync(alter); } catch { /* Already exists */ }
        }

        // Seed default Virtual Shelves and Smart Collections if empty
        var shelfCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(1) FROM VirtualShelves;");
        if (shelfCount == 0)
        {
            string now = DateTime.Now.ToString("O");
            await conn.ExecuteAsync("INSERT INTO VirtualShelves (Name, Icon, CreatedAt) VALUES ('Системна архітектура', '🏛', @Now), ('Високонавантажені системи', '🚀', @Now), ('Улюблене та Шпаргалки', '⭐', @Now);", new { Now = now });
        }

        var smartCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(1) FROM SmartCollections;");
        if (smartCount == 0)
        {
            await conn.ExecuteAsync("""
                INSERT INTO SmartCollections (Name, KeywordRule, FormatRule, StatusRule, AddedThisYearOnly, Icon)
                VALUES
                ('.NET + Не прочитано + Цього року', '.NET', NULL, 0, 1, '🔥'),
                ('Важкі тех-книги PDF', NULL, '.pdf', NULL, 0, '📕'),
                ('Активне читання (Читаю)', NULL, NULL, 1, 0, '📖');
                """);
        }
    }

    public async Task<List<RootFolderRecord>> GetRootFoldersAsync()
    {
        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync<RootFolderRecord>("SELECT * FROM RootFolders ORDER BY Path;");
        return rows.ToList();
    }

    public async Task AddRootFolderAsync(string path)
    {
        var normalized = Path.GetFullPath(path.Trim());
        await using var conn = CreateConnection();
        await conn.ExecuteAsync("""
            INSERT INTO RootFolders (Path, IsEnabled, AddedAt)
            VALUES (@Path, 1, @AddedAt)
            ON CONFLICT(Path) DO UPDATE SET IsEnabled = 1;
            """, new { Path = normalized, AddedAt = DateTime.Now.ToString("O") });
    }

    public async Task RemoveRootFolderAsync(long id, string path)
    {
        await using var conn = CreateConnection();
        await conn.ExecuteAsync("DELETE FROM RootFolders WHERE Id = @Id;", new { Id = id });
        await conn.ExecuteAsync("DELETE FROM Books WHERE FilePath LIKE @Prefix;", new { Prefix = path.TrimEnd('\\', '/') + "%" });
    }

    public async Task UpdateFolderScanTimeAsync(long id)
    {
        await using var conn = CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE RootFolders SET LastScannedAt = @Now WHERE Id = @Id;",
            new { Id = id, Now = DateTime.Now.ToString("O") });
    }

    public async Task<BookRecord?> GetBookByPathAsync(string filePath)
    {
        await using var conn = CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<BookRecord>(
            "SELECT * FROM Books WHERE FilePath = @FilePath;",
            new { FilePath = filePath });
    }

    public async Task<BookRecord?> GetBookByIdAsync(long id)
    {
        await using var conn = CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<BookRecord>(
            "SELECT * FROM Books WHERE Id = @Id;",
            new { Id = id });
    }

    public async Task UpdateBookFilePathAsync(long bookId, string newFilePath)
    {
        await using var conn = CreateConnection();
        var conflictId = await conn.ExecuteScalarAsync<long?>(
            "SELECT Id FROM Books WHERE FilePath = @FilePath AND Id <> @Id;",
            new { FilePath = newFilePath, Id = bookId });

        if (conflictId.HasValue)
        {
            await conn.ExecuteAsync("DELETE FROM Books WHERE Id = @ConflictId;", new { ConflictId = conflictId.Value });
        }

        await conn.ExecuteAsync(
            "UPDATE Books SET FilePath = @FilePath WHERE Id = @Id;",
            new { FilePath = newFilePath, Id = bookId });
    }

    public async Task<long> UpsertBookAsync(BookRecord book)
    {
        await using var conn = CreateConnection();

        var sql = """
            INSERT INTO Books (
                FilePath, FileHash, Title, Author, Category, Tags, Extension,
                FileSize, PageCount, CoverPath, Status, CurrentPage, ReadingProgress,
                FileModifiedAt, AddedAt, LastOpenedAt, Description,
                PublishYear, Isbn, Doi, Publisher, Language, Series, VolumeNumber, DetectedEncoding, IsFtsIndexed
            ) VALUES (
                @FilePath, @FileHash, @Title, @Author, @Category, @Tags, @Extension,
                @FileSize, @PageCount, @CoverPath, @Status, @CurrentPage, @ReadingProgress,
                @FileModifiedAt, @AddedAt, @LastOpenedAt, @Description,
                @PublishYear, @Isbn, @Doi, @Publisher, @Language, @Series, @VolumeNumber, @DetectedEncoding, @IsFtsIndexed
            )
            ON CONFLICT(FilePath) DO UPDATE SET
                FileHash = excluded.FileHash,
                Title = excluded.Title,
                Author = excluded.Author,
                Category = CASE WHEN Books.Category = 'Загальне' THEN excluded.Category ELSE Books.Category END,
                PageCount = excluded.PageCount,
                FileSize = excluded.FileSize,
                CoverPath = COALESCE(excluded.CoverPath, Books.CoverPath),
                FileModifiedAt = excluded.FileModifiedAt,
                Description = COALESCE(excluded.Description, Books.Description),
                PublishYear = COALESCE(excluded.PublishYear, Books.PublishYear),
                Isbn = COALESCE(excluded.Isbn, Books.Isbn),
                Doi = COALESCE(excluded.Doi, Books.Doi),
                Publisher = COALESCE(excluded.Publisher, Books.Publisher),
                Language = COALESCE(excluded.Language, Books.Language),
                Series = COALESCE(excluded.Series, Books.Series),
                VolumeNumber = COALESCE(excluded.VolumeNumber, Books.VolumeNumber),
                DetectedEncoding = COALESCE(excluded.DetectedEncoding, Books.DetectedEncoding)
            RETURNING Id;
            """;

        return await conn.ExecuteScalarAsync<long>(sql, new
        {
            book.FilePath,
            book.FileHash,
            book.Title,
            book.Author,
            book.Category,
            book.Tags,
            book.Extension,
            book.FileSize,
            book.PageCount,
            book.CoverPath,
            Status = (int)book.Status,
            book.CurrentPage,
            book.ReadingProgress,
            FileModifiedAt = book.FileModifiedAt.ToString("O"),
            AddedAt = book.AddedAt.ToString("O"),
            LastOpenedAt = book.LastOpenedAt?.ToString("O"),
            book.Description,
            book.PublishYear,
            book.Isbn,
            book.Doi,
            book.Publisher,
            book.Language,
            book.Series,
            book.VolumeNumber,
            book.DetectedEncoding,
            book.IsFtsIndexed
        });
    }

    public async Task UpdateBookFullMetadataAsync(BookRecord book)
    {
        await using var conn = CreateConnection();
        await conn.ExecuteAsync("""
            UPDATE Books
            SET Title = @Title,
                Author = @Author,
                Category = @Category,
                Tags = @Tags,
                Status = @Status,
                Description = @Description,
                PublishYear = @PublishYear,
                Isbn = @Isbn,
                Doi = @Doi,
                Publisher = @Publisher,
                Language = @Language,
                Series = @Series,
                VolumeNumber = @VolumeNumber,
                DetectedEncoding = @DetectedEncoding
            WHERE Id = @Id;
            """, new
        {
            book.Id,
            book.Title,
            book.Author,
            book.Category,
            book.Tags,
            Status = (int)book.Status,
            book.Description,
            book.PublishYear,
            book.Isbn,
            book.Doi,
            book.Publisher,
            book.Language,
            book.Series,
            book.VolumeNumber,
            book.DetectedEncoding
        });

        // Also update FTS metadata entry if indexed
        await conn.ExecuteAsync("""
            UPDATE BooksFts
            SET Title = @Title, Author = @Author, Tags = @Tags
            WHERE BookId = @BookId;
            """, new { BookId = book.Id.ToString(), book.Title, book.Author, book.Tags });
    }

    public async Task BatchUpdateBooksAsync(IEnumerable<long> bookIds, string? author, string? category, string? tagsToAppend, ReadingStatus? status, string? series)
    {
        var ids = bookIds.ToList();
        if (ids.Count == 0) return;

        await using var conn = CreateConnection();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        foreach (long id in ids)
        {
            var current = await conn.QueryFirstOrDefaultAsync<BookRecord>("SELECT * FROM Books WHERE Id = @Id;", new { Id = id }, tx);
            if (current == null) continue;

            string newAuthor = !string.IsNullOrWhiteSpace(author) ? author.Trim() : current.Author;
            string newCat = !string.IsNullOrWhiteSpace(category) ? category.Trim() : current.Category;
            string newSeries = !string.IsNullOrWhiteSpace(series) ? series.Trim() : (current.Series ?? string.Empty);
            ReadingStatus newStatus = status ?? current.Status;

            string newTags = current.Tags;
            if (!string.IsNullOrWhiteSpace(tagsToAppend))
            {
                var merged = (current.Tags + "," + tagsToAppend)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                newTags = string.Join(", ", merged);
            }

            await conn.ExecuteAsync("""
                UPDATE Books
                SET Author = @Author,
                    Category = @Category,
                    Tags = @Tags,
                    Status = @Status,
                    Series = @Series
                WHERE Id = @Id;
                """, new
            {
                Id = id,
                Author = newAuthor,
                Category = newCat,
                Tags = newTags,
                Status = (int)newStatus,
                Series = string.IsNullOrWhiteSpace(newSeries) ? null : newSeries
            }, tx);
        }

        await tx.CommitAsync();
    }

    // FTS5 Full-Text Indexing & Querying
    public async Task IndexBookFtsContentAsync(long bookId, string title, string author, string tags, string fullTextContent)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        await conn.ExecuteAsync("DELETE FROM BooksFts WHERE BookId = @BookId;", new { BookId = bookId.ToString() }, tx);
        await conn.ExecuteAsync("""
            INSERT INTO BooksFts (BookId, Title, Author, Tags, Content)
            VALUES (@BookId, @Title, @Author, @Tags, @Content);
            """, new
        {
            BookId = bookId.ToString(),
            Title = title ?? string.Empty,
            Author = author ?? string.Empty,
            Tags = tags ?? string.Empty,
            Content = fullTextContent ?? string.Empty
        }, tx);

        await conn.ExecuteAsync("UPDATE Books SET IsFtsIndexed = 1 WHERE Id = @Id;", new { Id = bookId }, tx);
        await tx.CommitAsync();
    }

    public async Task<HashSet<long>> SearchFtsBookIdsAsync(string ftsQuery)
    {
        var result = new HashSet<long>();
        if (string.IsNullOrWhiteSpace(ftsQuery)) return result;

        await using var conn = CreateConnection();
        try
        {
            var rows = await conn.QueryAsync<string>(
                "SELECT BookId FROM BooksFts WHERE BooksFts MATCH @Query LIMIT 500;",
                new { Query = ftsQuery });
            foreach (var r in rows)
            {
                if (long.TryParse(r, out long id))
                    result.Add(id);
            }
        }
        catch
        {
            // Fallback if user typed unbalanced quotes during live typing
            string safeQuery = "\"" + ftsQuery.Replace("\"", "") + "\"*";
            try
            {
                var rows = await conn.QueryAsync<string>(
                    "SELECT BookId FROM BooksFts WHERE BooksFts MATCH @Query LIMIT 500;",
                    new { Query = safeQuery });
                foreach (var r in rows)
                {
                    if (long.TryParse(r, out long id))
                        result.Add(id);
                }
            }
            catch
            {
                // Ignore invalid FTS syntax
            }
        }

        return result;
    }

    // Virtual Shelves
    public async Task<List<VirtualShelfRecord>> GetVirtualShelvesAsync()
    {
        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync<VirtualShelfRecord>("""
            SELECT s.*, COUNT(bs.BookId) AS BookCount
            FROM VirtualShelves s
            LEFT JOIN BookShelves bs ON bs.ShelfId = s.Id
            GROUP BY s.Id
            ORDER BY s.Name;
            """);
        return rows.ToList();
    }

    public async Task<long> CreateVirtualShelfAsync(string name, string icon = "📁")
    {
        await using var conn = CreateConnection();
        return await conn.ExecuteScalarAsync<long>("""
            INSERT INTO VirtualShelves (Name, Icon, CreatedAt)
            VALUES (@Name, @Icon, @Now)
            ON CONFLICT(Name) DO UPDATE SET Icon = excluded.Icon
            RETURNING Id;
            """, new { Name = name.Trim(), Icon = icon, Now = DateTime.Now.ToString("O") });
    }

    public async Task AssignBookToShelfAsync(long bookId, long shelfId)
    {
        await using var conn = CreateConnection();
        await conn.ExecuteAsync("DELETE FROM BookShelves WHERE BookId = @BookId;", new { BookId = bookId });
        if (shelfId > 0)
        {
            await conn.ExecuteAsync("""
                INSERT OR IGNORE INTO BookShelves (BookId, ShelfId)
                VALUES (@BookId, @ShelfId);
                """, new { BookId = bookId, ShelfId = shelfId });
        }
    }

    public async Task RemoveBookFromShelfAsync(long bookId)
    {
        await using var conn = CreateConnection();
        await conn.ExecuteAsync("DELETE FROM BookShelves WHERE BookId = @BookId;", new { BookId = bookId });
    }

    public async Task<HashSet<long>> GetBookIdsInShelfAsync(long shelfId)
    {
        await using var conn = CreateConnection();
        var ids = await conn.QueryAsync<long>("SELECT BookId FROM BookShelves WHERE ShelfId = @ShelfId;", new { ShelfId = shelfId });
        return ids.ToHashSet();
    }

    // Smart Collections
    public async Task<List<SmartCollectionRecord>> GetSmartCollectionsAsync()
    {
        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync<SmartCollectionRecord>("SELECT * FROM SmartCollections ORDER BY Id;");
        return rows.ToList();
    }

    public async Task CreateSmartCollectionAsync(SmartCollectionRecord sc)
    {
        await using var conn = CreateConnection();
        await conn.ExecuteAsync("""
            INSERT INTO SmartCollections (Name, KeywordRule, FormatRule, StatusRule, AddedThisYearOnly, Icon)
            VALUES (@Name, @KeywordRule, @FormatRule, @StatusRule, @AddedThisYearOnly, @Icon)
            ON CONFLICT(Name) DO UPDATE SET
                KeywordRule = excluded.KeywordRule,
                FormatRule = excluded.FormatRule,
                StatusRule = excluded.StatusRule,
                AddedThisYearOnly = excluded.AddedThisYearOnly;
            """, sc);
    }

    // Related Items
    public async Task<List<RelatedItemRecord>> GetRelatedItemsForBookAsync(long bookId)
    {
        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync<RelatedItemRecord>(
            "SELECT * FROM RelatedItems WHERE BookId = @BookId ORDER BY CreatedAt DESC;",
            new { BookId = bookId });
        return rows.ToList();
    }

    public async Task AddRelatedItemAsync(RelatedItemRecord item)
    {
        await using var conn = CreateConnection();
        await conn.ExecuteAsync("""
            INSERT INTO RelatedItems (BookId, RelatedBookId, Title, RelationType, TargetPathOrNote, CreatedAt)
            VALUES (@BookId, @RelatedBookId, @Title, @RelationType, @TargetPathOrNote, @Now);
            """, new
        {
            item.BookId,
            item.RelatedBookId,
            item.Title,
            item.RelationType,
            item.TargetPathOrNote,
            Now = DateTime.Now.ToString("O")
        });
    }

    public async Task DeleteRelatedItemAsync(long id)
    {
        await using var conn = CreateConnection();
        await conn.ExecuteAsync("DELETE FROM RelatedItems WHERE Id = @Id;", new { Id = id });
    }

    // Annotations & Bookmarks
    public async Task<List<AnnotationRecord>> GetAnnotationsForBookAsync(long bookId)
    {
        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync<AnnotationRecord>(
            "SELECT * FROM Annotations WHERE BookId = @BookId ORDER BY PageNumber ASC, CreatedAt ASC;",
            new { BookId = bookId });
        return rows.ToList();
    }

    public async Task<List<AnnotationRecord>> GetAllAnnotationsAsync()
    {
        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync<AnnotationRecord>("SELECT * FROM Annotations ORDER BY BookId, PageNumber;");
        return rows.ToList();
    }

    public async Task<long> AddAnnotationAsync(AnnotationRecord ann)
    {
        await using var conn = CreateConnection();
        return await conn.ExecuteScalarAsync<long>("""
            INSERT INTO Annotations (BookId, PageNumber, ScrollOffset, QuoteText, CommentText, HighlightColor, IsBookmark, CreatedAt)
            VALUES (@BookId, @PageNumber, @ScrollOffset, @QuoteText, @CommentText, @HighlightColor, @IsBookmark, @Now)
            RETURNING Id;
            """, new
        {
            ann.BookId,
            ann.PageNumber,
            ann.ScrollOffset,
            ann.QuoteText,
            ann.CommentText,
            ann.HighlightColor,
            IsBookmark = ann.IsBookmark ? 1 : 0,
            Now = DateTime.Now.ToString("O")
        });
    }

    public async Task DeleteAnnotationAsync(long id)
    {
        await using var conn = CreateConnection();
        await conn.ExecuteAsync("DELETE FROM Annotations WHERE Id = @Id;", new { Id = id });
    }

    // Duplicate Detection & Merging
    public async Task<List<DuplicateGroup>> FindDuplicatesAsync()
    {
        var allBooks = await GetAllBooksAsync();
        var groups = new List<DuplicateGroup>();

        // Group by FileHash + FileSize or Normalized Title
        var byHash = allBooks
            .GroupBy(b => $"{b.FileHash}_{b.FileSize}")
            .Where(g => g.Count() > 1);

        foreach (var g in byHash)
        {
            groups.Add(new DuplicateGroup
            {
                GroupKey = g.Key,
                Reason = $"Однаковий SHA-256 хеш та розмір ({g.First().FormattedSize})",
                Books = g.ToList()
            });
        }

        var byTitle = allBooks
            .GroupBy(b => b.Title.Trim().ToLowerInvariant())
            .Where(g => g.Count() > 1 && !groups.Any(existing => existing.Books.Select(x => x.Id).Intersect(g.Select(y => y.Id)).Count() > 1));

        foreach (var g in byTitle)
        {
            groups.Add(new DuplicateGroup
            {
                GroupKey = g.Key,
                Reason = $"Збіг назви книги («{g.First().Title}»)",
                Books = g.ToList()
            });
        }

        return groups;
    }

    public async Task MergeDuplicateBooksAsync(long primaryBookId, IEnumerable<long> duplicateBookIds)
    {
        var dupIds = duplicateBookIds.Where(id => id != primaryBookId).ToList();
        if (dupIds.Count == 0) return;

        await using var conn = CreateConnection();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        var primary = await conn.QueryFirstOrDefaultAsync<BookRecord>("SELECT * FROM Books WHERE Id = @Id;", new { Id = primaryBookId }, tx);
        if (primary == null) return;

        foreach (var dupId in dupIds)
        {
            var dup = await conn.QueryFirstOrDefaultAsync<BookRecord>("SELECT * FROM Books WHERE Id = @Id;", new { Id = dupId }, tx);
            if (dup == null) continue;

            // Merge tags
            var mergedTags = (primary.Tags + "," + dup.Tags)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            primary.Tags = string.Join(", ", mergedTags);

            if (dup.ReadingProgress > primary.ReadingProgress)
            {
                primary.CurrentPage = dup.CurrentPage;
                primary.ReadingProgress = dup.ReadingProgress;
            }

            // Re-point annotations, shelves, and related items to primaryBookId
            await conn.ExecuteAsync("UPDATE Annotations SET BookId = @PrimaryId WHERE BookId = @DupId;", new { PrimaryId = primaryBookId, DupId = dupId }, tx);
            await conn.ExecuteAsync("UPDATE OR IGNORE BookShelves SET BookId = @PrimaryId WHERE BookId = @DupId;", new { PrimaryId = primaryBookId, DupId = dupId }, tx);
            await conn.ExecuteAsync("DELETE FROM BookShelves WHERE BookId = @DupId;", new { DupId = dupId }, tx);
            await conn.ExecuteAsync("UPDATE RelatedItems SET BookId = @PrimaryId WHERE BookId = @DupId;", new { PrimaryId = primaryBookId, DupId = dupId }, tx);
            await conn.ExecuteAsync("DELETE FROM BooksFts WHERE BookId = @DupId;", new { DupId = dupId.ToString() }, tx);
            await conn.ExecuteAsync("DELETE FROM Books WHERE Id = @DupId;", new { DupId = dupId }, tx);
        }

        await conn.ExecuteAsync("""
            UPDATE Books
            SET Tags = @Tags, CurrentPage = @CurrentPage, ReadingProgress = @ReadingProgress
            WHERE Id = @Id;
            """, new { primary.Id, primary.Tags, primary.CurrentPage, primary.ReadingProgress }, tx);

        await tx.CommitAsync();
    }

    public async Task<List<BookRecord>> GetAllBooksAsync()
    {
        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync<BookRecord>("""
            SELECT b.*, bs.ShelfId AS VirtualShelfId, s.Name AS VirtualShelfName
            FROM Books b
            LEFT JOIN BookShelves bs ON bs.BookId = b.Id
            LEFT JOIN VirtualShelves s ON s.Id = bs.ShelfId
            ORDER BY COALESCE(b.LastOpenedAt, b.AddedAt) DESC, b.Title ASC;
            """);
        return rows.ToList();
    }

    public async Task UpdateReadingProgressAsync(long bookId, int currentPage, int totalPages, ReadingStatus? statusOverride = null)
    {
        double progress = totalPages > 0 ? Math.Clamp(Math.Round((double)currentPage / totalPages * 100.0, 1), 0, 100) : 0;
        await using var conn = CreateConnection();

        if (statusOverride.HasValue)
        {
            await conn.ExecuteAsync("""
                UPDATE Books
                SET CurrentPage = @CurrentPage,
                    PageCount = CASE WHEN @TotalPages > 0 THEN @TotalPages ELSE PageCount END,
                    ReadingProgress = @Progress,
                    Status = @Status,
                    LastOpenedAt = @Now
                WHERE Id = @Id;
                """, new
            {
                Id = bookId,
                CurrentPage = Math.Max(1, currentPage),
                TotalPages = totalPages,
                Progress = progress,
                Status = (int)statusOverride.Value,
                Now = DateTime.Now.ToString("O")
            });
        }
        else
        {
            await conn.ExecuteAsync("""
                UPDATE Books
                SET CurrentPage = @CurrentPage,
                    PageCount = CASE WHEN @TotalPages > 0 THEN @TotalPages ELSE PageCount END,
                    ReadingProgress = @Progress,
                    Status = CASE
                        WHEN @Progress >= 99.5 THEN 2
                        WHEN Status = 0 AND @CurrentPage > 1 THEN 1
                        ELSE Status
                    END,
                    LastOpenedAt = @Now
                WHERE Id = @Id;
                """, new
            {
                Id = bookId,
                CurrentPage = Math.Max(1, currentPage),
                TotalPages = totalPages,
                Progress = progress,
                Now = DateTime.Now.ToString("O")
            });
        }
    }

    public async Task UpdateBookStatusAsync(long bookId, ReadingStatus status)
    {
        await using var conn = CreateConnection();
        await conn.ExecuteAsync("""
            UPDATE Books
            SET Status = @Status
            WHERE Id = @Id;
            """, new
        {
            Id = bookId,
            Status = (int)status
        });
    }

    public async Task UpdateBookStatusAndTagsAsync(long bookId, ReadingStatus status, string category, string tags)
    {
        await using var conn = CreateConnection();
        await conn.ExecuteAsync("""
            UPDATE Books
            SET Status = @Status,
                Category = @Category,
                Tags = @Tags
            WHERE Id = @Id;
            """, new
        {
            Id = bookId,
            Status = (int)status,
            Category = string.IsNullOrWhiteSpace(category) ? "Загальне" : category.Trim(),
            Tags = tags?.Trim() ?? string.Empty
        });
    }

    public async Task DeleteBookRecordAsync(long bookId)
    {
        await using var conn = CreateConnection();
        await conn.ExecuteAsync("DELETE FROM Books WHERE Id = @Id;", new { Id = bookId });
        await conn.ExecuteAsync("DELETE FROM BooksFts WHERE BookId = @BookId;", new { BookId = bookId.ToString() });
    }
}
