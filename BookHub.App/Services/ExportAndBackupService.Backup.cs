using System.IO;
using System.IO.Compression;
using System.Text;
using BookHub.Core.Data;

namespace BookHub.Core.Services;

public static class CatalogBackupService
{
    /// <summary>
    /// Creates a local one-click .zip backup containing bookhub.db, settings.json, exported Obsidian Markdown notes, and cover cache.
    /// </summary>
    public static async Task<string> CreateOneClickBackupZipAsync(LibraryDatabase database, string? targetZipPath = null)
    {
        string rootDir = LibraryDatabase.DataDirectory;
        string backupsDir = Path.Combine(rootDir, "Backups");
        Directory.CreateDirectory(backupsDir);

        string zipPath = targetZipPath ?? Path.Combine(backupsDir, $"ReaderHub_Backup_{DateTime.Now:yyyyMMdd_HHmmss}.zip");
        if (File.Exists(zipPath)) File.Delete(zipPath);

        var books = await database.GetAllBooksAsync();
        var allAnnotations = await database.GetAllAnnotationsAsync();

        await Task.Run(() =>
        {
            using var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var archive = new ZipArchive(fs, ZipArchiveMode.Create);

            // 1. Copy SQLite DB safely (with FileShare.ReadWrite)
            if (File.Exists(database.DatabasePath))
            {
                AddFileSharedToArchive(archive, database.DatabasePath, "bookhub.db");
            }

            // 2. Copy settings.json
            string settingsPath = Path.Combine(rootDir, "settings.json");
            if (File.Exists(settingsPath))
            {
                AddFileSharedToArchive(archive, settingsPath, "settings.json");
            }

            // 3. Export Markdown notes for every book with annotations
            var grouped = allAnnotations.GroupBy(a => a.BookId).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var b in books)
            {
                if (grouped.TryGetValue(b.Id, out var anns) && anns.Count > 0)
                {
                    string safeName = string.Join("_", b.Title.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
                    if (safeName.Length > 60) safeName = safeName[..60];
                    var entry = archive.CreateEntry($"ObsidianNotes/{safeName}_{b.Id}.md");
                    using var entryStream = entry.Open();
                    using var sw = new StreamWriter(entryStream, new UTF8Encoding(false));

                    string tempMd = Path.Combine(rootDir, ".temp", $"note_{b.Id}.md");
                    Directory.CreateDirectory(Path.GetDirectoryName(tempMd)!);
                    ExportAndBackupService.ExportAnnotationsToMarkdown(b, anns, tempMd);
                    sw.Write(File.ReadAllText(tempMd, Encoding.UTF8));
                    File.Delete(tempMd);
                }
            }

            // 4. Include cached cover thumbnails
            if (Directory.Exists(database.CacheDirectory))
            {
                foreach (var coverFile in Directory.EnumerateFiles(database.CacheDirectory, "*.png"))
                {
                    AddFileSharedToArchive(archive, coverFile, $"cache/{Path.GetFileName(coverFile)}");
                }
            }
        });

        return zipPath;
    }

    private static void AddFileSharedToArchive(ZipArchive archive, string sourceFilePath, string entryName)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
        using var srcStream = new FileStream(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var destStream = entry.Open();
        srcStream.CopyTo(destStream);
    }
}
