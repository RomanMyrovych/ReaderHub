using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using BookHub.Core.Data;
using BookHub.Core.Models;
using BookHub.Core.Parsers;

namespace BookHub.Core.Services;

public record ScanProgressInfo(int ProcessedCount, int TotalDiscovered, string CurrentFile, bool IsCompleted);

public sealed class FileScannerService : IDisposable
{
    private readonly LibraryDatabase _database;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private bool _disposed;

    public bool ScanEbooksEnabled { get; set; } = true;
    public bool ScanDocumentsEnabled { get; set; } = true;
    public bool EnableBackgroundFileWatcher { get; set; } = true;

    // Managed Library (\libofbooks) configuration
    public bool IsManagedLibraryEnabled { get; set; }
    public bool ManagedLibraryMoveFiles { get; set; }
    public string ManagedLibraryPath { get; set; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "libofbooks");

    private static readonly HashSet<string> EbookExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".epub", ".fb2", ".cbz"
    };

    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".odt", ".rtf", ".txt", ".md", ".html", ".htm"
    };

    public event Action<BookRecord>? BookAddedOrUpdated;
    public event Action<ScanProgressInfo>? ScanProgressChanged;

    public FileScannerService(LibraryDatabase database)
    {
        _database = database;
    }

    public bool IsExtensionAllowedByFilter(string ext)
    {
        if (string.IsNullOrWhiteSpace(ext)) return false;
        if (ScanEbooksEnabled && EbookExtensions.Contains(ext)) return true;
        if (ScanDocumentsEnabled && DocumentExtensions.Contains(ext)) return true;
        return ScanEbooksEnabled && ScanDocumentsEnabled && BookFormatParser.SupportedExtensions.Contains(ext);
    }

    public async Task RefreshWatchersAsync()
    {
        ClearWatchers();
        if (!EnableBackgroundFileWatcher)
            return;

        var folders = await _database.GetRootFoldersAsync();
        foreach (var folder in folders.Where(f => f.IsEnabled && Directory.Exists(f.Path)))
        {
            try
            {
                var watcher = new FileSystemWatcher(folder.Path)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                    EnableRaisingEvents = true
                };

                watcher.Created += OnFileCreatedOrRenamed;
                watcher.Renamed += OnFileCreatedOrRenamed;
                _watchers.Add(watcher);
            }
            catch
            {
                // Ignore inaccessible directory watcher failure
            }
        }
    }

    private void OnFileCreatedOrRenamed(object sender, FileSystemEventArgs e)
    {
        if (!EnableBackgroundFileWatcher || string.IsNullOrWhiteSpace(e.FullPath)) return;
        var ext = Path.GetExtension(e.FullPath);
        if (!IsExtensionAllowedByFilter(ext)) return;

        // Run background import after a brief delay so file copy finishes
        _ = Task.Run(async () =>
        {
            await Task.Delay(1200);
            if (File.Exists(e.FullPath))
            {
                var book = await ProcessSingleFileAsync(e.FullPath);
                if (book != null)
                {
                    BookAddedOrUpdated?.Invoke(book);
                }
            }
        });
    }

    public async Task ScanAllFoldersAsync(CancellationToken ct = default)
    {
        if (!await _scanLock.WaitAsync(0, ct))
            return;

        try
        {
            var folders = await _database.GetRootFoldersAsync();
            var candidateFiles = new List<string>();

            foreach (var folder in folders.Where(f => f.IsEnabled && Directory.Exists(f.Path)))
            {
                await Task.Run(() => CollectSupportedFilesRecursive(folder.Path, candidateFiles, ct), ct);
            }

            if (IsManagedLibraryEnabled && !string.IsNullOrWhiteSpace(ManagedLibraryPath))
            {
                try
                {
                    Directory.CreateDirectory(ManagedLibraryPath);
                }
                catch { }

                // Also consolidate any already-cataloged books from outside \libofbooks
                var existingBooks = await _database.GetAllBooksAsync();
                foreach (var eb in existingBooks)
                {
                    if (!string.IsNullOrWhiteSpace(eb.FilePath) && File.Exists(eb.FilePath))
                    {
                        candidateFiles.Add(eb.FilePath);
                    }
                }

                if (Directory.Exists(ManagedLibraryPath))
                {
                    await Task.Run(() => CollectSupportedFilesRecursive(ManagedLibraryPath, candidateFiles, ct), ct);
                }
            }

            candidateFiles = candidateFiles
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            int total = candidateFiles.Count;
            int processed = 0;

            foreach (var filePath in candidateFiles)
            {
                ct.ThrowIfCancellationRequested();
                processed++;
                ScanProgressChanged?.Invoke(new ScanProgressInfo(processed, total, Path.GetFileName(filePath), false));

                var added = await ProcessSingleFileAsync(filePath, ct);
                if (added != null)
                {
                    BookAddedOrUpdated?.Invoke(added);
                }
            }

            foreach (var folder in folders)
            {
                await _database.UpdateFolderScanTimeAsync(folder.Id);
            }

            ScanProgressChanged?.Invoke(new ScanProgressInfo(processed, total, string.Empty, true));
        }
        finally
        {
            _scanLock.Release();
        }
    }

    private void CollectSupportedFilesRecursive(string rootPath, List<string> targetList, CancellationToken ct)
    {
        var stack = new Stack<string>();
        stack.Push(rootPath);

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var currentDir = stack.Pop();

            // Skip internal cache/packages folders
            var dirName = Path.GetFileName(currentDir);
            if (dirName.Equals("cache", StringComparison.OrdinalIgnoreCase) ||
                dirName.Equals("packages", StringComparison.OrdinalIgnoreCase) ||
                dirName.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                dirName.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                dirName.StartsWith('.'))
            {
                continue;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(currentDir))
                {
                    var ext = Path.GetExtension(file);
                    if (IsExtensionAllowedByFilter(ext))
                    {
                        targetList.Add(file);
                    }
                }

                foreach (var subDir in Directory.EnumerateDirectories(currentDir))
                {
                    stack.Push(subDir);
                }
            }
            catch
            {
                // Ignore permission denied on system subfolders
            }
        }
    }

    public async Task<string> ConsolidateFileToManagedLibraryAsync(string sourceFilePath, CancellationToken ct = default)
    {
        if (!IsManagedLibraryEnabled || string.IsNullOrWhiteSpace(ManagedLibraryPath) || string.IsNullOrWhiteSpace(sourceFilePath))
            return sourceFilePath;

        try
        {
            if (!File.Exists(sourceFilePath))
                return sourceFilePath;

            string libDir = Path.GetFullPath(ManagedLibraryPath);
            Directory.CreateDirectory(libDir);

            string fullSource = Path.GetFullPath(sourceFilePath);
            string libDirWithSep = libDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (fullSource.StartsWith(libDirWithSep, StringComparison.OrdinalIgnoreCase))
            {
                return fullSource;
            }

            string fileName = Path.GetFileName(fullSource);
            string destPath = Path.Combine(libDir, fileName);

            if (File.Exists(destPath))
            {
                var srcInfo = new FileInfo(fullSource);
                var dstInfo = new FileInfo(destPath);
                string srcHash = await Task.Run(() => ComputeFastPartialHash(fullSource, srcInfo.Length), ct);
                string dstHash = await Task.Run(() => ComputeFastPartialHash(destPath, dstInfo.Length), ct);

                if (!string.Equals(srcHash, dstHash, StringComparison.OrdinalIgnoreCase))
                {
                    string stem = Path.GetFileNameWithoutExtension(fileName);
                    string ext = Path.GetExtension(fileName);
                    string shortHash = srcHash.Length >= 6 ? srcHash[..6] : "copy";
                    destPath = Path.Combine(libDir, $"{stem}_{shortHash}{ext}");
                }
            }

            if (!File.Exists(destPath))
            {
                if (ManagedLibraryMoveFiles)
                {
                    await Task.Run(() =>
                    {
                        try
                        {
                            File.Move(fullSource, destPath);
                        }
                        catch
                        {
                            File.Copy(fullSource, destPath, overwrite: false);
                            try { File.Delete(fullSource); } catch { }
                        }
                    }, ct);
                }
                else
                {
                    await Task.Run(() => File.Copy(fullSource, destPath, overwrite: false), ct);
                }
            }
            else if (ManagedLibraryMoveFiles && !string.Equals(fullSource, destPath, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(fullSource); } catch { }
            }

            var existingOld = await _database.GetBookByPathAsync(sourceFilePath)
                              ?? await _database.GetBookByPathAsync(fullSource);
            if (existingOld != null && !string.Equals(existingOld.FilePath, destPath, StringComparison.OrdinalIgnoreCase))
            {
                await _database.UpdateBookFilePathAsync(existingOld.Id, destPath);
            }

            return destPath;
        }
        catch
        {
            return sourceFilePath;
        }
    }

    public async Task<BookRecord?> ProcessSingleFileAsync(string filePath, CancellationToken ct = default)
    {
        try
        {
            var initialFi = new FileInfo(filePath);
            if (!initialFi.Exists || initialFi.Length == 0) return null;

            if (!BookFormatParser.ValidateFileFormatSignature(filePath, out _))
                return null;

            if (IsManagedLibraryEnabled)
            {
                filePath = await ConsolidateFileToManagedLibraryAsync(filePath, ct);
            }

            var fi = new FileInfo(filePath);
            if (!fi.Exists || fi.Length == 0) return null;

            // Fast check if already indexed with same LastWriteTime & Size
            var existing = await _database.GetBookByPathAsync(filePath);
            if (existing != null &&
                existing.FileSize == fi.Length &&
                Math.Abs((existing.FileModifiedAt - fi.LastWriteTime).TotalSeconds) < 2 &&
                !string.IsNullOrWhiteSpace(existing.CoverPath) &&
                File.Exists(existing.CoverPath))
            {
                return null; // Unchanged
            }

            string fileHash = await Task.Run(() => ComputeFastPartialHash(filePath, fi.Length), ct);
            var parsed = await Task.Run(() => BookFormatParser.ParseMetadataAndCover(filePath), ct);

            string coverFilePath = Path.Combine(_database.CacheDirectory, $"{fileHash}.png");
            if (!File.Exists(coverFilePath))
            {
                await Task.Run(() => SaveOrGenerateCoverImage(coverFilePath, parsed.CoverImageBytes, parsed.Title, parsed.Author, fi.Extension), ct);
            }

            var record = new BookRecord
            {
                Id = existing?.Id ?? 0,
                FilePath = filePath,
                FileHash = fileHash,
                Title = string.IsNullOrWhiteSpace(parsed.Title) ? Path.GetFileNameWithoutExtension(filePath) : parsed.Title,
                Author = string.IsNullOrWhiteSpace(parsed.Author) ? "Невідомий автор" : parsed.Author,
                Category = existing != null && existing.Category != "Загальне" ? existing.Category : parsed.Category,
                Tags = existing != null && !string.IsNullOrWhiteSpace(existing.Tags) ? existing.Tags : parsed.Tags,
                Extension = fi.Extension.ToLowerInvariant(),
                FileSize = fi.Length,
                PageCount = Math.Max(1, parsed.PageCount),
                CoverPath = coverFilePath,
                Status = existing?.Status ?? ReadingStatus.None,
                CurrentPage = existing?.CurrentPage ?? 1,
                ReadingProgress = existing?.ReadingProgress ?? 0,
                FileModifiedAt = fi.LastWriteTime,
                AddedAt = existing?.AddedAt ?? DateTime.Now,
                LastOpenedAt = existing?.LastOpenedAt,
                Description = parsed.Description,
                PublishYear = existing?.PublishYear ?? parsed.PublishYear,
                Isbn = existing?.Isbn ?? parsed.Isbn,
                Doi = existing?.Doi ?? parsed.Doi,
                Publisher = existing?.Publisher ?? parsed.Publisher,
                Language = existing?.Language ?? parsed.Language ?? "uk",
                Series = existing?.Series ?? parsed.Series,
                VolumeNumber = existing?.VolumeNumber ?? parsed.VolumeNumber,
                DetectedEncoding = parsed.DetectedEncoding ?? "UTF-8",
                IsFtsIndexed = 1
            };

            record.Id = await _database.UpsertBookAsync(record);

            // Index text content in SQLite FTS5 virtual table
            string ftsText = await Task.Run(() => BookFormatParser.ExtractTextForFtsIndex(filePath), ct);
            await _database.IndexBookFtsContentAsync(record.Id, record.Title, record.Author, record.Tags, ftsText);

            return record;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Computes SHA-256 of the first 2 MB + file size to avoid blocking I/O on huge 500MB+ PDFs while preventing duplicates.
    /// </summary>
    public static string ComputeFastPartialHash(string filePath, long fileSize)
    {
        const int maxBytesToHash = 2 * 1024 * 1024; // 2 MB
        using var sha = SHA256.Create();
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536);

        int toRead = (int)Math.Min(fileSize, maxBytesToHash);
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(toRead);
        try
        {
            int read = fs.Read(buffer, 0, toRead);
            sha.TransformBlock(buffer, 0, read, null, 0);
            byte[] sizeBytes = BitConverter.GetBytes(fileSize);
            sha.TransformFinalBlock(sizeBytes, 0, sizeBytes.Length);
            return Convert.ToHexString(sha.Hash!)[..24].ToLowerInvariant();
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void SaveOrGenerateCoverImage(string targetPath, byte[]? rawCoverBytes, string title, string author, string extension)
    {
        if (rawCoverBytes is { Length: > 64 })
        {
            try
            {
                using var ms = new MemoryStream(rawCoverBytes);
                using var srcBmp = Image.FromStream(ms);
                int targetW = 280;
                int targetH = (int)Math.Clamp(Math.Round((double)srcBmp.Height / srcBmp.Width * targetW), 200, 440);
                using var resized = new Bitmap(targetW, targetH);
                using (var g = Graphics.FromImage(resized))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.DrawImage(srcBmp, 0, 0, targetW, targetH);
                }
                resized.Save(targetPath, ImageFormat.Png);
                return;
            }
            catch
            {
                // Fallback to generated cover below
            }
        }

        // Generate modern stylized dark-themed book cover thumbnail
        int width = 280;
        int height = 390;
        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var gfx = Graphics.FromImage(bmp);
        gfx.SmoothingMode = SmoothingMode.AntiAlias;
        gfx.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        string extUpper = extension.TrimStart('.').ToUpperInvariant();
        (Color c1, Color c2, Color accent) = extUpper switch
        {
            "PDF" => (Color.FromArgb(42, 22, 30), Color.FromArgb(20, 14, 22), Color.FromArgb(239, 68, 68)),
            "EPUB" => (Color.FromArgb(18, 38, 44), Color.FromArgb(14, 22, 30), Color.FromArgb(16, 185, 129)),
            "FB2" => (Color.FromArgb(36, 28, 52), Color.FromArgb(18, 16, 30), Color.FromArgb(168, 85, 247)),
            "DOCX" => (Color.FromArgb(20, 32, 56), Color.FromArgb(14, 20, 36), Color.FromArgb(59, 130, 246)),
            _ => (Color.FromArgb(34, 36, 46), Color.FromArgb(18, 20, 28), Color.FromArgb(245, 158, 11))
        };

        using (var bgBrush = new LinearGradientBrush(new Rectangle(0, 0, width, height), c1, c2, LinearGradientMode.ForwardDiagonal))
        {
            gfx.FillRectangle(bgBrush, 0, 0, width, height);
        }

        // Spine & top accent bar
        using (var accentBrush = new SolidBrush(accent))
        {
            gfx.FillRectangle(accentBrush, 0, 0, 10, height);
            gfx.FillRectangle(accentBrush, 26, 26, 56, 24);
        }

        using var badgeFont = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        using var whiteBrush = new SolidBrush(Color.White);
        gfx.DrawString(extUpper, badgeFont, whiteBrush, new PointF(34, 30));

        using var titleFont = new Font("Segoe UI Semibold", 13.5f, FontStyle.Bold);
        var titleRect = new RectangleF(26, 72, width - 44, 210);
        gfx.DrawString(title.Length > 110 ? title[..107] + "..." : title, titleFont, whiteBrush, titleRect);

        using var authorFont = new Font("Segoe UI", 10f, FontStyle.Regular);
        using var mutedBrush = new SolidBrush(Color.FromArgb(190, 200, 215));
        var authorRect = new RectangleF(26, height - 76, width - 44, 54);
        gfx.DrawString(author, authorFont, mutedBrush, authorRect);

        using var borderPen = new Pen(Color.FromArgb(60, 255, 255, 255), 1.5f);
        gfx.DrawRectangle(borderPen, 1, 1, width - 2, height - 2);

        bmp.Save(targetPath, ImageFormat.Png);
    }

    private void ClearWatchers()
    {
        foreach (var w in _watchers)
        {
            try
            {
                w.EnableRaisingEvents = false;
                w.Dispose();
            }
            catch
            {
                // Ignore
            }
        }
        _watchers.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ClearWatchers();
        _scanLock.Dispose();
    }
}
