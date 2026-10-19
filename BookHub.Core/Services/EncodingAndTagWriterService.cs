using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using BookHub.Core.Models;

namespace BookHub.Core.Services;

public static class EncodingAndTagWriterService
{
    static EncodingAndTagWriterService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static readonly string[] SupportedEncodingNames =
    [
        "UTF-8",
        "UTF-8 (BOM)",
        "windows-1251",
        "KOI8-U",
        "iso-8859-5",
        "ibm866"
    ];

    public static (string Text, string EncodingName) ReadTextWithAutoEncoding(string filePath, int maxBytes = int.MaxValue)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        int length = (int)Math.Min(fs.Length, maxBytes);
        byte[] bytes = new byte[length];
        int totalRead = 0;
        while (totalRead < length)
        {
            int r = fs.Read(bytes, totalRead, length - totalRead);
            if (r <= 0) break;
            totalRead += r;
        }

        var (enc, encName) = DetectEncodingFromBytes(bytes, totalRead);
        int bomOffset = 0;
        if (totalRead >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            bomOffset = 3;
        else if (totalRead >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
            bomOffset = 2;

        return (enc.GetString(bytes, bomOffset, totalRead - bomOffset), encName);
    }

    public static (Encoding Encoding, string Name) DetectEncodingFromBytes(byte[] buffer, int length)
    {
        if (length >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
            return (new UTF8Encoding(true), "UTF-8 (BOM)");

        if (length >= 2 && buffer[0] == 0xFF && buffer[1] == 0xFE)
            return (Encoding.Unicode, "UTF-16LE");

        if (length >= 2 && buffer[0] == 0xFE && buffer[1] == 0xFF)
            return (Encoding.BigEndianUnicode, "UTF-16BE");

        // Check XML/HTML declaration header if ASCII
        int headerLen = Math.Min(length, 512);
        string asciiHeader = Encoding.ASCII.GetString(buffer, 0, headerLen);
        var match = Regex.Match(asciiHeader, @"encoding=[""']([A-Za-z0-9_\-]+)[""']", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            string declared = match.Groups[1].Value.Trim();
            try
            {
                var enc = Encoding.GetEncoding(declared);
                return (enc, enc.WebName.ToUpperInvariant());
            }
            catch
            {
                // Continue heuristic
            }
        }

        // Strict UTF-8 validation
        if (IsValidUtf8(buffer, length))
            return (new UTF8Encoding(false), "UTF-8");

        // Heuristic scoring for Cyrillic 8-bit encodings: Windows-1251, KOI8-U, CP866, ISO-8859-5
        string[] candidates = ["windows-1251", "koi8-u", "ibm866", "iso-8859-5"];
        Encoding bestEnc = Encoding.GetEncoding("windows-1251");
        string bestName = "windows-1251";
        int bestScore = int.MinValue;

        int sampleLen = Math.Min(length, 32768);
        foreach (var name in candidates)
        {
            try
            {
                var enc = Encoding.GetEncoding(name);
                string sample = enc.GetString(buffer, 0, sampleLen);
                int score = ScoreCyrillicText(sample);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestEnc = enc;
                    bestName = name;
                }
            }
            catch
            {
                // Ignore
            }
        }

        return (bestEnc, bestName);
    }

    private static bool IsValidUtf8(byte[] bytes, int length)
    {
        int i = 0;
        while (i < length)
        {
            byte b = bytes[i];
            if (b <= 0x7F)
            {
                i++;
                continue;
            }

            int extraBytes;
            if ((b & 0xE0) == 0xC0) extraBytes = 1;
            else if ((b & 0xF0) == 0xE0) extraBytes = 2;
            else if ((b & 0xF8) == 0xF0) extraBytes = 3;
            else return false;

            if (i + extraBytes >= length)
            {
                // Truncated at buffer boundary is acceptable if near end
                return true;
            }

            for (int j = 1; j <= extraBytes; j++)
            {
                if ((bytes[i + j] & 0xC0) != 0x80)
                    return false;
            }
            i += extraBytes + 1;
        }
        return true;
    }

    private static int ScoreCyrillicText(string text)
    {
        int score = 0;
        foreach (char c in text)
        {
            if (c is >= 'а' and <= 'я' || c is 'і' or 'ї' or 'є' or 'ґ')
                score += 4;
            else if (c is >= 'А' and <= 'Я' || c is 'І' or 'Ї' or 'Є' or 'Ґ')
                score += 2;
            else if (char.IsControl(c) && c is not ('\r' or '\n' or '\t'))
                score -= 10;
            else if (c is >= '\u2500' and <= '\u257F') // Box drawing characters common when CP1251 is misread as CP866
                score -= 5;
        }
        return score;
    }

    /// <summary>
    /// Converts a legacy/broken encoded text file (TXT, FB2, HTML, MD, RTF, CSV) to clean UTF-8 with an automatic .bak backup.
    /// </summary>
    public static string ConvertFileToCleanUtf8WithBackup(string filePath, string? sourceEncodingOverride = null, bool includeBom = false)
    {
        byte[] rawBytes = File.ReadAllBytes(filePath);
        Encoding srcEnc;
        if (!string.IsNullOrWhiteSpace(sourceEncodingOverride))
        {
            string cleanName = sourceEncodingOverride.Replace("(BOM)", "").Trim();
            srcEnc = Encoding.GetEncoding(cleanName);
        }
        else
        {
            (srcEnc, _) = DetectEncodingFromBytes(rawBytes, rawBytes.Length);
        }

        int offset = 0;
        if (rawBytes.Length >= 3 && rawBytes[0] == 0xEF && rawBytes[1] == 0xBB && rawBytes[2] == 0xBF)
            offset = 3;

        string decoded = srcEnc.GetString(rawBytes, offset, rawBytes.Length - offset);

        // If XML/FB2/HTML has an old encoding="..." attribute, update it to utf-8
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext is ".fb2" or ".xml" or ".html" or ".htm")
        {
            decoded = Regex.Replace(
                decoded,
                @"encoding=[""'][A-Za-z0-9_\-]+([""'])",
                "encoding=\"utf-8\"",
                RegexOptions.IgnoreCase);
        }

        string backupPath = filePath + ".bak";
        if (!File.Exists(backupPath))
        {
            File.Copy(filePath, backupPath, overwrite: false);
        }

        File.WriteAllText(filePath, decoded, new UTF8Encoding(includeBom));
        return backupPath;
    }

    /// <summary>
    /// Writes updated metadata tags back into FB2 or EPUB files on disk when requested by the user.
    /// </summary>
    public static bool TryWriteMetadataToFile(BookRecord book, out string statusMessage)
    {
        string ext = book.Extension.ToLowerInvariant();
        try
        {
            if (ext == ".fb2")
            {
                return WriteMetadataToFb2(book, out statusMessage);
            }
            if (ext == ".epub")
            {
                return WriteMetadataToEpub(book, out statusMessage);
            }

            statusMessage = $"Збережено у базу даних SQLite (прямий запис у файл підтримується для FB2 та EPUB).";
            return true;
        }
        catch (Exception ex)
        {
            statusMessage = $"Метадані збережено в SQLite, проте запис у файл не вдався: {ex.Message}";
            return false;
        }
    }

    private static bool WriteMetadataToFb2(BookRecord book, out string statusMessage)
    {
        var (xmlText, _) = ReadTextWithAutoEncoding(book.FilePath);
        var doc = XDocument.Parse(xmlText, LoadOptions.PreserveWhitespace);
        XNamespace fb = doc.Root?.GetDefaultNamespace() ?? "http://www.gribuser.ru/xml/fictionbook/2.0";

        var titleInfo = doc.Descendants(fb + "title-info").FirstOrDefault();
        if (titleInfo == null)
        {
            statusMessage = "Не знайдено блок <title-info> у FB2.";
            return false;
        }

        // Book Title
        var bookTitleEl = titleInfo.Element(fb + "book-title");
        if (bookTitleEl == null)
        {
            bookTitleEl = new XElement(fb + "book-title", book.Title);
            titleInfo.Add(bookTitleEl);
        }
        else
        {
            bookTitleEl.Value = book.Title;
        }

        // Author(s)
        titleInfo.Elements(fb + "author").Remove();
        var authors = book.Author.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var a in authors)
        {
            var parts = a.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var authorEl = new XElement(fb + "author");
            if (parts.Length == 1)
            {
                authorEl.Add(new XElement(fb + "last-name", parts[0]));
            }
            else
            {
                authorEl.Add(new XElement(fb + "first-name", parts[0]));
                authorEl.Add(new XElement(fb + "last-name", string.Join(" ", parts.Skip(1))));
            }
            titleInfo.AddFirst(authorEl);
        }

        // Sequence (Series & Volume)
        titleInfo.Elements(fb + "sequence").Remove();
        if (!string.IsNullOrWhiteSpace(book.Series))
        {
            var seq = new XElement(fb + "sequence", new XAttribute("name", book.Series));
            if (book.VolumeNumber.HasValue)
                seq.SetAttributeValue("number", book.VolumeNumber.Value);
            titleInfo.Add(seq);
        }

        // Annotation / Description
        if (!string.IsNullOrWhiteSpace(book.Description))
        {
            var ann = titleInfo.Element(fb + "annotation");
            if (ann == null)
            {
                ann = new XElement(fb + "annotation");
                titleInfo.Add(ann);
            }
            ann.RemoveNodes();
            ann.Add(new XElement(fb + "p", book.Description));
        }

        string backup = book.FilePath + ".bak";
        if (!File.Exists(backup))
            File.Copy(book.FilePath, backup, false);

        using var fs = new FileStream(book.FilePath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var sw = new StreamWriter(fs, new UTF8Encoding(false));
        doc.Save(sw);

        statusMessage = "Метадані успішно записано у файл FB2 та базу SQLite.";
        return true;
    }

    private static bool WriteMetadataToEpub(BookRecord book, out string statusMessage)
    {
        string backup = book.FilePath + ".bak";
        if (!File.Exists(backup))
            File.Copy(book.FilePath, backup, false);

        using var archive = ZipFile.Open(book.FilePath, ZipArchiveMode.Update);
        var opfEntry = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith(".opf", StringComparison.OrdinalIgnoreCase));
        if (opfEntry == null)
        {
            statusMessage = "Не знайдено OPF-пакет у EPUB.";
            return false;
        }

        XDocument opfDoc;
        using (var s = opfEntry.Open())
        {
            opfDoc = XDocument.Load(s);
        }

        XNamespace dc = "http://purl.org/dc/elements/1.1/";
        var metadataEl = opfDoc.Descendants().FirstOrDefault(e => e.Name.LocalName == "metadata");
        if (metadataEl == null)
        {
            statusMessage = "Не знайдено секцію <metadata> у EPUB.";
            return false;
        }

        var titleEl = metadataEl.Elements(dc + "title").FirstOrDefault();
        if (titleEl != null) titleEl.Value = book.Title;
        else metadataEl.Add(new XElement(dc + "title", book.Title));

        var creatorEl = metadataEl.Elements(dc + "creator").FirstOrDefault();
        if (creatorEl != null) creatorEl.Value = book.Author;
        else metadataEl.Add(new XElement(dc + "creator", book.Author));

        if (!string.IsNullOrWhiteSpace(book.Description))
        {
            var descEl = metadataEl.Elements(dc + "description").FirstOrDefault();
            if (descEl != null) descEl.Value = book.Description;
            else metadataEl.Add(new XElement(dc + "description", book.Description));
        }

        if (!string.IsNullOrWhiteSpace(book.Publisher))
        {
            var pubEl = metadataEl.Elements(dc + "publisher").FirstOrDefault();
            if (pubEl != null) pubEl.Value = book.Publisher;
            else metadataEl.Add(new XElement(dc + "publisher", book.Publisher));
        }

        using (var s = opfEntry.Open())
        {
            s.SetLength(0);
            opfDoc.Save(s);
        }

        statusMessage = "Метадані успішно записано у файл EPUB (.opf) та базу SQLite.";
        return true;
    }
}
