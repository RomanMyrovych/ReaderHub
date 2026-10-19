using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using BookHub.Core.Models;
using BookHub.Core.Pdf;
using BookHub.Core.Services;
using VersOne.Epub;

namespace BookHub.Core.Parsers;

public static partial class BookFormatParser
{
    public static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".epub", ".mobi", ".azw", ".azw3", ".fb2", ".djvu",
        ".cbr", ".cbz", ".doc", ".docx", ".odt", ".rtf", ".txt",
        ".html", ".htm", ".md", ".csv", ".ppt", ".pptx", ".xls", ".xlsx"
    };

    static BookFormatParser()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static ParsedBookMetadata ParseMetadataAndCover(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        var fallbackTitle = CleanFileNameTitle(Path.GetFileNameWithoutExtension(filePath));

        try
        {
            return ext switch
            {
                ".pdf" => ParsePdf(filePath, fallbackTitle),
                ".epub" => ParseEpub(filePath, fallbackTitle),
                ".fb2" => ParseFb2(filePath, fallbackTitle),
                ".docx" => ParseDocx(filePath, fallbackTitle),
                ".odt" => ParseOdt(filePath, fallbackTitle),
                ".pptx" => ParsePptx(filePath, fallbackTitle),
                ".xlsx" => ParseXlsx(filePath, fallbackTitle),
                ".cbz" => ParseCbz(filePath, fallbackTitle),
                ".html" or ".htm" => ParseHtml(filePath, fallbackTitle),
                ".md" or ".txt" or ".csv" or ".rtf" => ParsePlainTextOrRtf(filePath, fallbackTitle, ext),
                _ => ParseBinaryBookHeader(filePath, fallbackTitle, ext)
            };
        }
        catch
        {
            return new ParsedBookMetadata
            {
                Title = fallbackTitle,
                Author = "Невідомий автор",
                Category = InferCategoryFromPath(filePath),
                PageCount = EstimatePagesByFileSize(filePath)
            };
        }
    }

    public static string ExtractTextForFtsIndex(string filePath, bool enableOcrHeuristic = true)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        try
        {
            if (ext == ".pdf")
            {
                using var session = new PdfDocumentSession(filePath);
                string text = session.ExtractTextForFtsIndexing(maxPagesToScan: 20);
                if (string.IsNullOrWhiteSpace(text) && enableOcrHeuristic)
                {
                    text = $"[OCR-Scan Ready] {session.Title} {session.Author} {Path.GetFileNameWithoutExtension(filePath)}";
                }
                return text;
            }

            var (pages, _) = LoadDocumentPaginatedContent(filePath);
            var sb = new StringBuilder();
            foreach (var p in pages.Take(15))
            {
                sb.AppendLine(p);
                if (sb.Length > 48000) break;
            }
            return sb.ToString();
        }
        catch
        {
            return Path.GetFileNameWithoutExtension(filePath);
        }
    }

    private static ParsedBookMetadata ParsePdf(string filePath, string fallbackTitle)
    {
        using var session = new PdfDocumentSession(filePath);
        byte[]? coverBytes = null;
        try
        {
            if (session.PageCount > 0)
            {
                coverBytes = session.RenderCoverPngBytes(300);
            }
        }
        catch
        {
            // Ignore thumbnail error
        }

        string title = !string.IsNullOrWhiteSpace(session.Title) && session.Title.Length > 2
            ? session.Title
            : fallbackTitle;

        string author = !string.IsNullOrWhiteSpace(session.Author)
            ? session.Author
            : "Невідомий автор";

        string firstPageSample = session.ExtractPageText(1);
        var (isbn, doi, year) = ExtractIsbnDoiYear(firstPageSample);

        return new ParsedBookMetadata
        {
            Title = title,
            Author = author,
            Category = InferCategoryFromPath(filePath),
            PageCount = Math.Max(1, session.PageCount),
            CoverImageBytes = coverBytes,
            Description = firstPageSample.Length > 500 ? firstPageSample[..500] + "..." : firstPageSample,
            Isbn = isbn,
            Doi = doi,
            PublishYear = year,
            DetectedEncoding = "PDF Binary"
        };
    }

    private static ParsedBookMetadata ParseEpub(string filePath, string fallbackTitle)
    {
        using var bookRef = EpubReader.OpenBook(filePath);
        string title = !string.IsNullOrWhiteSpace(bookRef.Title) ? bookRef.Title.Trim() : fallbackTitle;
        string author = bookRef.AuthorList is { Count: > 0 }
            ? string.Join(", ", bookRef.AuthorList.Where(a => !string.IsNullOrWhiteSpace(a)))
            : (!string.IsNullOrWhiteSpace(bookRef.Author) ? bookRef.Author : "Невідомий автор");

        byte[]? coverBytes = null;
        try
        {
            coverBytes = bookRef.ReadCover();
        }
        catch
        {
            // Cover optional
        }

        int readingOrderCount = 1;
        try
        {
            var readingOrder = bookRef.GetReadingOrder();
            readingOrderCount = Math.Max(1, readingOrder.Count);
        }
        catch
        {
            readingOrderCount = EstimatePagesByFileSize(filePath);
        }

        string desc = StripHtml(bookRef.Description ?? string.Empty);
        var (isbn, doi, year) = ExtractIsbnDoiYear(desc);

        return new ParsedBookMetadata
        {
            Title = title,
            Author = string.IsNullOrWhiteSpace(author) ? "Невідомий автор" : author,
            Category = InferCategoryFromPath(filePath),
            PageCount = readingOrderCount,
            Description = desc,
            CoverImageBytes = coverBytes,
            Isbn = isbn,
            Doi = doi,
            PublishYear = year,
            DetectedEncoding = "UTF-8 (EPUB)"
        };
    }

    public static ParsedBookMetadata ParseFb2(string filePath, string fallbackTitle)
    {
        var (_, detectedEncName) = EncodingAndTagWriterService.ReadTextWithAutoEncoding(filePath, maxBytes: 16384);

        var meta = new ParsedBookMetadata
        {
            Title = fallbackTitle,
            Author = "Невідомий автор",
            Category = InferCategoryFromPath(filePath),
            DetectedEncoding = detectedEncName
        };

        string? coverId = null;
        bool inTitleInfo = false;
        bool inPublishInfo = false;
        bool inAuthor = false;
        string firstName = string.Empty;
        string middleName = string.Empty;
        string lastName = string.Empty;
        var authors = new List<string>();
        var genres = new List<string>();
        string? description = null;

        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536);
        var settings = new XmlReaderSettings
        {
            IgnoreComments = true,
            IgnoreWhitespace = true,
            DtdProcessing = DtdProcessing.Ignore,
            CheckCharacters = false
        };

        using var reader = XmlReader.Create(fs, settings);
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                string localName = reader.LocalName;

                if (localName == "title-info") inTitleInfo = true;
                else if (localName == "publish-info") inPublishInfo = true;
                else if (inTitleInfo && localName == "book-title")
                {
                    string bt = reader.ReadElementContentAsString().Trim();
                    if (!string.IsNullOrWhiteSpace(bt)) meta.Title = bt;
                }
                else if (inTitleInfo && localName == "genre")
                {
                    string g = reader.ReadElementContentAsString().Trim();
                    if (!string.IsNullOrWhiteSpace(g)) genres.Add(g);
                }
                else if (inTitleInfo && localName == "lang")
                {
                    string lang = reader.ReadElementContentAsString().Trim();
                    if (!string.IsNullOrWhiteSpace(lang)) meta.Language = lang;
                }
                else if (inTitleInfo && localName == "sequence")
                {
                    meta.Series = reader.GetAttribute("name");
                    if (int.TryParse(reader.GetAttribute("number"), out int vol))
                        meta.VolumeNumber = vol;
                }
                else if (inPublishInfo && localName == "publisher")
                {
                    meta.Publisher = reader.ReadElementContentAsString().Trim();
                }
                else if (inPublishInfo && localName == "year")
                {
                    if (int.TryParse(reader.ReadElementContentAsString().Trim(), out int yr))
                        meta.PublishYear = yr;
                }
                else if (inPublishInfo && localName == "isbn")
                {
                    meta.Isbn = reader.ReadElementContentAsString().Trim();
                }
                else if (inTitleInfo && localName == "author")
                {
                    inAuthor = true;
                    firstName = middleName = lastName = string.Empty;
                }
                else if (inTitleInfo && inAuthor && localName == "first-name")
                {
                    firstName = reader.ReadElementContentAsString().Trim();
                }
                else if (inTitleInfo && inAuthor && localName == "middle-name")
                {
                    middleName = reader.ReadElementContentAsString().Trim();
                }
                else if (inTitleInfo && inAuthor && localName == "last-name")
                {
                    lastName = reader.ReadElementContentAsString().Trim();
                }
                else if (inTitleInfo && localName == "annotation")
                {
                    using var sub = reader.ReadSubtree();
                    var sb = new StringBuilder();
                    while (sub.Read())
                    {
                        if (sub.NodeType is XmlNodeType.Text or XmlNodeType.CDATA)
                            sb.Append(sub.Value).Append(' ');
                    }
                    description = sb.ToString().Trim();
                }
                else if (inTitleInfo && localName == "image")
                {
                    for (int i = 0; i < reader.AttributeCount; i++)
                    {
                        reader.MoveToAttribute(i);
                        if (reader.LocalName == "href" && !string.IsNullOrWhiteSpace(reader.Value))
                        {
                            coverId = reader.Value.TrimStart('#');
                        }
                    }
                    reader.MoveToElement();
                }
                else if (localName == "binary")
                {
                    string? binId = reader.GetAttribute("id");
                    string? contentType = reader.GetAttribute("content-type") ?? "";
                    bool isTargetCover = (coverId != null && string.Equals(binId, coverId, StringComparison.OrdinalIgnoreCase))
                                         || (coverId == null && meta.CoverImageBytes == null && contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase));

                    if (isTargetCover && meta.CoverImageBytes == null)
                    {
                        string base64 = reader.ReadElementContentAsString();
                        try
                        {
                            meta.CoverImageBytes = Convert.FromBase64String(base64.Trim());
                            if (coverId != null)
                                break;
                        }
                        catch
                        {
                            // Invalid base64
                        }
                    }
                    else if (coverId == null)
                    {
                        break;
                    }
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (reader.LocalName == "author" && inAuthor)
                {
                    inAuthor = false;
                    var full = string.Join(" ", new[] { firstName, middleName, lastName }.Where(s => !string.IsNullOrWhiteSpace(s)));
                    if (!string.IsNullOrWhiteSpace(full))
                        authors.Add(full);
                }
                else if (reader.LocalName == "title-info") inTitleInfo = false;
                else if (reader.LocalName == "publish-info") inPublishInfo = false;
            }
        }

        if (authors.Count > 0)
            meta.Author = string.Join(", ", authors.Distinct());
        if (genres.Count > 0)
            meta.Tags = string.Join(", ", genres.Distinct().Take(5));
        meta.Description = description;
        meta.PageCount = EstimatePagesByFileSize(filePath, bytesPerPage: 2800);

        return meta;
    }

    public static (List<string> Pages, List<TocItem> Toc) LoadFb2PaginatedContent(string filePath, int charsPerPage = 3500)
    {
        var pages = new List<string>();
        var toc = new List<TocItem>();
        var currentBuilder = new StringBuilder();

        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536);
        var settings = new XmlReaderSettings
        {
            IgnoreComments = true,
            IgnoreWhitespace = true,
            DtdProcessing = DtdProcessing.Ignore,
            CheckCharacters = false
        };

        using var reader = XmlReader.Create(fs, settings);
        bool inBody = false;
        bool inTitle = false;
        var titleBuilder = new StringBuilder();

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                string name = reader.LocalName;
                if (name == "body") inBody = true;
                else if (name == "binary") break;
                else if (inBody && name == "title")
                {
                    inTitle = true;
                    titleBuilder.Clear();
                    currentBuilder.AppendLine();
                }
                else if (inBody && name is "p" or "v" or "subtitle" or "text-author")
                {
                    if (!inTitle) currentBuilder.Append("    ");
                }
                else if (inBody && name == "empty-line")
                {
                    currentBuilder.AppendLine();
                }
            }
            else if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA && inBody)
            {
                if (inTitle) titleBuilder.Append(reader.Value).Append(' ');
                else currentBuilder.Append(reader.Value);
            }
            else if (reader.NodeType == XmlNodeType.EndElement && inBody)
            {
                string name = reader.LocalName;
                if (name == "title" && inTitle)
                {
                    inTitle = false;
                    string sectionTitle = Regex.Replace(titleBuilder.ToString(), @"\s+", " ").Trim();
                    if (!string.IsNullOrWhiteSpace(sectionTitle))
                    {
                        if (currentBuilder.Length > 400)
                        {
                            pages.Add(currentBuilder.ToString().Trim());
                            currentBuilder.Clear();
                        }

                        int targetPage = pages.Count + 1;
                        toc.Add(new TocItem { Title = sectionTitle, PageNumber = targetPage });
                        currentBuilder.AppendLine($"=== {sectionTitle.ToUpperInvariant()} ===").AppendLine();
                    }
                }
                else if (name is "p" or "v" or "subtitle" or "text-author")
                {
                    if (!inTitle)
                    {
                        currentBuilder.AppendLine().AppendLine();
                        if (currentBuilder.Length >= charsPerPage)
                        {
                            pages.Add(currentBuilder.ToString().Trim());
                            currentBuilder.Clear();
                        }
                    }
                }
                else if (name == "body") inBody = false;
            }
        }

        if (currentBuilder.Length > 0)
            pages.Add(currentBuilder.ToString().Trim());

        if (pages.Count == 0)
            pages.Add("Текст документа порожній або не містить розділів <body>.");

        return (pages, toc);
    }

    public static (List<string> Pages, List<TocItem> Toc) LoadEpubPaginatedContent(string filePath)
    {
        var pages = new List<string>();
        var toc = new List<TocItem>();

        using var bookRef = EpubReader.OpenBook(filePath);
        var readingOrder = bookRef.GetReadingOrder();
        var fileMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in readingOrder)
        {
            string html = item.ReadContentAsText();
            string cleanText = StripHtmlPreserveParagraphs(html);
            if (string.IsNullOrWhiteSpace(cleanText)) continue;

            int startPage = pages.Count + 1;
            fileMap[item.FilePath] = startPage;
            fileMap[Path.GetFileName(item.FilePath)] = startPage;

            foreach (var chunk in SplitTextIntoPages(cleanText, 3800))
            {
                pages.Add(chunk);
            }
        }

        try
        {
            var navItems = bookRef.GetNavigation();
            if (navItems != null)
            {
                foreach (var nav in navItems)
                {
                    toc.Add(ConvertEpubNav(nav, fileMap));
                }
            }
        }
        catch
        {
            // Navigation optional
        }

        if (pages.Count == 0)
            pages.Add("Не вдалося витягти текстові розділи з EPUB.");

        return (pages, toc);
    }

    private static TocItem ConvertEpubNav(EpubNavigationItemRef nav, Dictionary<string, int> fileMap)
    {
        int pageNum = 1;
        string? filePath = nav.Link?.ContentFilePath;
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            if (!fileMap.TryGetValue(filePath, out pageNum))
            {
                fileMap.TryGetValue(Path.GetFileName(filePath), out pageNum);
            }
        }
        if (pageNum <= 0) pageNum = 1;

        var item = new TocItem
        {
            Title = string.IsNullOrWhiteSpace(nav.Title) ? "Розділ" : nav.Title.Trim(),
            PageNumber = pageNum
        };

        if (nav.NestedItems != null)
        {
            foreach (var child in nav.NestedItems)
            {
                item.Children.Add(ConvertEpubNav(child, fileMap));
            }
        }

        return item;
    }

    public static (List<string> Pages, List<TocItem> Toc) LoadDocumentPaginatedContent(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext == ".fb2") return LoadFb2PaginatedContent(filePath);
        if (ext == ".epub") return LoadEpubPaginatedContent(filePath);

        string fullText;
        var toc = new List<TocItem>();

        if (ext == ".docx")
        {
            fullText = ExtractDocxText(filePath, toc);
        }
        else if (ext == ".odt")
        {
            fullText = ExtractOdtText(filePath);
        }
        else if (ext == ".pptx")
        {
            fullText = ExtractPptxText(filePath, toc);
        }
        else if (ext == ".xlsx")
        {
            fullText = ExtractXlsxText(filePath, toc);
        }
        else if (ext is ".html" or ".htm")
        {
            var (raw, _) = EncodingAndTagWriterService.ReadTextWithAutoEncoding(filePath);
            fullText = StripHtmlPreserveParagraphs(raw);
        }
        else if (ext == ".rtf")
        {
            var (raw, _) = EncodingAndTagWriterService.ReadTextWithAutoEncoding(filePath);
            fullText = StripRtfToPlainText(raw);
        }
        else if (ext is ".txt" or ".md" or ".csv")
        {
            var (raw, _) = EncodingAndTagWriterService.ReadTextWithAutoEncoding(filePath);
            fullText = raw;
            if (ext == ".md")
            {
                ExtractMarkdownHeadings(fullText, toc);
            }
        }
        else
        {
            fullText = ExtractBinaryFormatSummary(filePath, ext);
        }

        var pages = SplitTextIntoPages(fullText, 3800);
        if (pages.Count == 0)
            pages.Add("Документ порожній.");

        return (pages, toc);
    }

    private static void ExtractMarkdownHeadings(string md, List<TocItem> toc)
    {
        using var sr = new StringReader(md);
        string? line;
        int charPos = 0;
        while ((line = sr.ReadLine()) != null)
        {
            if (line.StartsWith('#'))
            {
                string title = line.TrimStart('#', ' ').Trim();
                if (!string.IsNullOrWhiteSpace(title))
                {
                    toc.Add(new TocItem { Title = title, PageNumber = Math.Max(1, (charPos / 3800) + 1) });
                }
            }
            charPos += line.Length + 2;
        }
    }

    private static ParsedBookMetadata ParseDocx(string filePath, string fallbackTitle)
    {
        var meta = new ParsedBookMetadata
        {
            Title = fallbackTitle,
            Author = "Невідомий автор",
            Category = InferCategoryFromPath(filePath),
            PageCount = EstimatePagesByFileSize(filePath, 4096),
            DetectedEncoding = "OpenXML (UTF-8)"
        };

        using var archive = ZipFile.OpenRead(filePath);
        ReadOpenXmlCoreAndAppProps(archive, meta);

        if (string.IsNullOrWhiteSpace(meta.Description))
        {
            meta.Description = ExtractDocxText(filePath, null, maxChars: 600);
        }

        return meta;
    }

    private static ParsedBookMetadata ParseOdt(string filePath, string fallbackTitle)
    {
        string text = ExtractOdtText(filePath, maxChars: 1200);
        return new ParsedBookMetadata
        {
            Title = fallbackTitle,
            Author = "OpenDocument (ODT)",
            Category = InferCategoryFromPath(filePath),
            PageCount = EstimatePagesByFileSize(filePath, 3500),
            Description = text.Length > 500 ? text[..500] + "..." : text,
            DetectedEncoding = "ODF XML (UTF-8)"
        };
    }

    private static ParsedBookMetadata ParsePptx(string filePath, string fallbackTitle)
    {
        var toc = new List<TocItem>();
        string text = ExtractPptxText(filePath, toc);
        return new ParsedBookMetadata
        {
            Title = fallbackTitle,
            Author = "Презентація PowerPoint",
            Category = InferCategoryFromPath(filePath),
            PageCount = Math.Max(1, toc.Count),
            Description = text.Length > 500 ? text[..500] + "..." : text,
            DetectedEncoding = "OpenXML Slides"
        };
    }

    private static ParsedBookMetadata ParseXlsx(string filePath, string fallbackTitle)
    {
        var toc = new List<TocItem>();
        string text = ExtractXlsxText(filePath, toc);
        return new ParsedBookMetadata
        {
            Title = fallbackTitle,
            Author = "Таблиця Excel",
            Category = InferCategoryFromPath(filePath),
            PageCount = Math.Max(1, toc.Count),
            Description = text.Length > 500 ? text[..500] + "..." : text,
            DetectedEncoding = "OpenXML Workbook"
        };
    }

    private static ParsedBookMetadata ParseCbz(string filePath, string fallbackTitle)
    {
        using var archive = ZipFile.OpenRead(filePath);
        var imageEntries = archive.Entries
            .Where(e => e.Name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                        e.Name.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                        e.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                        e.Name.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName)
            .ToList();

        byte[]? coverBytes = null;
        if (imageEntries.Count > 0)
        {
            using var s = imageEntries[0].Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            coverBytes = ms.ToArray();
        }

        return new ParsedBookMetadata
        {
            Title = fallbackTitle,
            Author = "Комікс / Манґа (CBZ)",
            Category = InferCategoryFromPath(filePath),
            PageCount = Math.Max(1, imageEntries.Count),
            CoverImageBytes = coverBytes,
            Description = $"Архів зображень CBZ ({imageEntries.Count} ілюстрацій).",
            DetectedEncoding = "CBZ Archive"
        };
    }

    private static ParsedBookMetadata ParsePlainTextOrRtf(string filePath, string fallbackTitle, string ext)
    {
        var (rawText, encName) = EncodingAndTagWriterService.ReadTextWithAutoEncoding(filePath, maxBytes: 32768);
        string clean = ext == ".rtf" ? StripRtfToPlainText(rawText) : rawText;

        string title = fallbackTitle;
        using var sr = new StringReader(clean);
        string? firstLine = sr.ReadLine()?.Trim();
        if (!string.IsNullOrWhiteSpace(firstLine))
        {
            if (firstLine.StartsWith("# "))
                title = firstLine[2..].Trim();
            else if (firstLine.Length is > 3 and < 95)
                title = firstLine;
        }

        var (isbn, doi, year) = ExtractIsbnDoiYear(clean);

        return new ParsedBookMetadata
        {
            Title = title,
            Author = ext == ".md" ? "Markdown Документ" : "Текстовий документ",
            Category = InferCategoryFromPath(filePath),
            PageCount = EstimatePagesByFileSize(filePath, 3200),
            Description = clean.Length > 450 ? clean[..450].Trim() + "..." : clean.Trim(),
            DetectedEncoding = encName,
            Isbn = isbn,
            Doi = doi,
            PublishYear = year
        };
    }

    private static ParsedBookMetadata ParseBinaryBookHeader(string filePath, string fallbackTitle, string ext)
    {
        string formatName = ext.TrimStart('.').ToUpperInvariant();
        return new ParsedBookMetadata
        {
            Title = fallbackTitle,
            Author = $"Формат {formatName}",
            Category = InferCategoryFromPath(filePath),
            PageCount = EstimatePagesByFileSize(filePath, 4096),
            Description = $"Локальний документ формату {formatName} ({Path.GetFileName(filePath)}).",
            DetectedEncoding = $"{formatName} Binary"
        };
    }

    private static void ReadOpenXmlCoreAndAppProps(ZipArchive archive, ParsedBookMetadata meta)
    {
        var coreEntry = archive.GetEntry("docProps/core.xml");
        if (coreEntry != null)
        {
            using var s = coreEntry.Open();
            using var xr = XmlReader.Create(s, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
            while (xr.Read())
            {
                if (xr.NodeType == XmlNodeType.Element)
                {
                    if (xr.LocalName == "title")
                    {
                        var t = xr.ReadElementContentAsString().Trim();
                        if (!string.IsNullOrWhiteSpace(t)) meta.Title = t;
                    }
                    else if (xr.LocalName == "creator")
                    {
                        var a = xr.ReadElementContentAsString().Trim();
                        if (!string.IsNullOrWhiteSpace(a)) meta.Author = a;
                    }
                    else if (xr.LocalName == "description")
                    {
                        var d = xr.ReadElementContentAsString().Trim();
                        if (!string.IsNullOrWhiteSpace(d)) meta.Description = d;
                    }
                }
            }
        }

        var appEntry = archive.GetEntry("docProps/app.xml");
        if (appEntry != null)
        {
            using var s = appEntry.Open();
            using var xr = XmlReader.Create(s, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
            while (xr.Read())
            {
                if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "Pages")
                {
                    if (int.TryParse(xr.ReadElementContentAsString(), out int pages) && pages > 0)
                        meta.PageCount = pages;
                }
            }
        }
    }

    private static string ExtractDocxText(string filePath, List<TocItem>? toc, int maxChars = int.MaxValue)
    {
        using var archive = ZipFile.OpenRead(filePath);
        var docEntry = archive.GetEntry("word/document.xml");
        if (docEntry == null) return string.Empty;

        var sb = new StringBuilder();
        using var s = docEntry.Open();
        using var xr = XmlReader.Create(s, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });

        var paraBuilder = new StringBuilder();
        bool isHeading = false;

        while (xr.Read())
        {
            if (xr.NodeType == XmlNodeType.Element)
            {
                if (xr.LocalName == "p")
                {
                    paraBuilder.Clear();
                    isHeading = false;
                }
                else if (xr.LocalName == "pStyle")
                {
                    var val = xr.GetAttribute("w:val") ?? xr.GetAttribute("val") ?? "";
                    if (val.Contains("Heading", StringComparison.OrdinalIgnoreCase) || val.StartsWith('1') || val.StartsWith('2'))
                        isHeading = true;
                }
                else if (xr.LocalName == "t")
                {
                    paraBuilder.Append(xr.ReadElementContentAsString());
                }
            }
            else if (xr.NodeType == XmlNodeType.EndElement && xr.LocalName == "p")
            {
                string line = paraBuilder.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(line))
                {
                    if (isHeading && toc != null && line.Length < 120)
                    {
                        int approxPage = Math.Max(1, (sb.Length / 3800) + 1);
                        toc.Add(new TocItem { Title = line, PageNumber = approxPage });
                    }
                    sb.AppendLine(line).AppendLine();
                    if (sb.Length >= maxChars) break;
                }
            }
        }

        return sb.ToString().Trim();
    }

    private static string ExtractOdtText(string filePath, int maxChars = int.MaxValue)
    {
        using var archive = ZipFile.OpenRead(filePath);
        var contentEntry = archive.GetEntry("content.xml");
        if (contentEntry == null) return string.Empty;

        var sb = new StringBuilder();
        using var s = contentEntry.Open();
        using var xr = XmlReader.Create(s, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
        while (xr.Read())
        {
            if (xr.NodeType == XmlNodeType.Text)
            {
                sb.Append(xr.Value).Append(' ');
                if (sb.Length >= maxChars) break;
            }
            else if (xr.NodeType == XmlNodeType.EndElement && xr.LocalName == "p")
            {
                sb.AppendLine().AppendLine();
            }
        }
        return sb.ToString().Trim();
    }

    private static string ExtractPptxText(string filePath, List<TocItem> toc)
    {
        using var archive = ZipFile.OpenRead(filePath);
        var slideEntries = archive.Entries
            .Where(e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase) && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName)
            .ToList();

        var sb = new StringBuilder();
        int slideNum = 0;
        foreach (var entry in slideEntries)
        {
            slideNum++;
            toc.Add(new TocItem { Title = $"Слайд {slideNum}", PageNumber = slideNum });
            sb.AppendLine($"=== СЛАЙД {slideNum} ===");
            using var s = entry.Open();
            using var xr = XmlReader.Create(s, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
            while (xr.Read())
            {
                if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "t")
                {
                    sb.AppendLine(xr.ReadElementContentAsString());
                }
            }
            sb.AppendLine();
        }
        return sb.ToString().Trim();
    }

    private static string ExtractXlsxText(string filePath, List<TocItem> toc)
    {
        using var archive = ZipFile.OpenRead(filePath);
        var sharedStrings = new List<string>();
        var ssEntry = archive.GetEntry("xl/sharedStrings.xml");
        if (ssEntry != null)
        {
            using var s = ssEntry.Open();
            using var xr = XmlReader.Create(s, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
            while (xr.Read())
            {
                if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "t")
                    sharedStrings.Add(xr.ReadElementContentAsString());
            }
        }

        toc.Add(new TocItem { Title = "Аркуш даних 1", PageNumber = 1 });
        return sharedStrings.Count > 0
            ? string.Join(" | ", sharedStrings)
            : "Табличні дані Excel (XLSX).";
    }

    private static string ExtractBinaryFormatSummary(string filePath, string ext)
    {
        var fi = new FileInfo(filePath);
        var sb = new StringBuilder();
        sb.AppendLine($"=== {Path.GetFileName(filePath)} ===");
        sb.AppendLine($"Формат: {ext.ToUpperInvariant()} • Розмір: {fi.Length:N0} байтів");
        sb.AppendLine();

        // Extract printable UTF-8 / ASCII text strings for MOBI/AZW/DJVU/DOC/PPT/XLS preview
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        int toRead = (int)Math.Min(fs.Length, 131072);
        byte[] buf = new byte[toRead];
        int read = fs.Read(buf, 0, toRead);

        var (enc, _) = EncodingAndTagWriterService.DetectEncodingFromBytes(buf, read);
        string raw = enc.GetString(buf, 0, read);
        var matches = Regex.Matches(raw, @"[\p{L}\p{N}\s\.,;:\-\(\)\!\?""'\/]{16,}");
        foreach (Match m in matches.Take(60))
        {
            string line = Regex.Replace(m.Value, @"\s+", " ").Trim();
            if (line.Length >= 16)
                sb.AppendLine(line);
        }

        return sb.ToString();
    }

    private static string StripRtfToPlainText(string rtf)
    {
        if (string.IsNullOrWhiteSpace(rtf)) return string.Empty;
        string s = Regex.Replace(rtf, @"\\par\b", "\n");
        s = Regex.Replace(s, @"\\[a-zA-Z]+\d*\s?", string.Empty);
        s = s.Replace("{", string.Empty).Replace("}", string.Empty);
        return s.Trim();
    }

    private static ParsedBookMetadata ParseHtml(string filePath, string fallbackTitle)
    {
        var (headChunk, encName) = EncodingAndTagWriterService.ReadTextWithAutoEncoding(filePath, maxBytes: 32768);

        string title = fallbackTitle;
        var titleMatch = HtmlTitleRegex().Match(headChunk);
        if (titleMatch.Success && !string.IsNullOrWhiteSpace(titleMatch.Groups[1].Value))
        {
            title = System.Net.WebUtility.HtmlDecode(titleMatch.Groups[1].Value.Trim());
        }

        string author = "Невідомий автор";
        var authorMatch = HtmlAuthorRegex().Match(headChunk);
        if (authorMatch.Success && !string.IsNullOrWhiteSpace(authorMatch.Groups[1].Value))
        {
            author = System.Net.WebUtility.HtmlDecode(authorMatch.Groups[1].Value.Trim());
        }

        string preview = StripHtml(headChunk);
        if (preview.Length > 400) preview = preview[..400] + "...";

        return new ParsedBookMetadata
        {
            Title = title,
            Author = author,
            Category = InferCategoryFromPath(filePath),
            PageCount = EstimatePagesByFileSize(filePath, 3500),
            Description = preview,
            DetectedEncoding = encName
        };
    }

    private static (string? Isbn, string? Doi, int? Year) ExtractIsbnDoiYear(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, null, null);

        string? isbn = null;
        var isbnMatch = Regex.Match(text, @"ISBN(?:-1[03])?:?\s*([0-9Xx\- ]{10,17})", RegexOptions.IgnoreCase);
        if (isbnMatch.Success)
            isbn = isbnMatch.Groups[1].Value.Trim();

        string? doi = null;
        var doiMatch = Regex.Match(text, @"\b(10\.\d{4,9}/[-._;()/:A-Za-z0-9]+)\b");
        if (doiMatch.Success)
            doi = doiMatch.Groups[1].Value.Trim();

        int? year = null;
        var yearMatch = Regex.Match(text, @"\b(19[7-9]\d|20[0-2]\d)\b");
        if (yearMatch.Success && int.TryParse(yearMatch.Groups[1].Value, out int y))
            year = y;

        return (isbn, doi, year);
    }

    private static List<string> SplitTextIntoPages(string text, int charsPerPage)
    {
        var pages = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return pages;

        var paragraphs = text.Split(["\r\n\r\n", "\n\n"], StringSplitOptions.RemoveEmptyEntries);
        var current = new StringBuilder();

        foreach (var p in paragraphs)
        {
            if (current.Length + p.Length > charsPerPage && current.Length > 0)
            {
                pages.Add(current.ToString().Trim());
                current.Clear();
            }
            current.AppendLine(p.Trim()).AppendLine();
        }

        if (current.Length > 0)
            pages.Add(current.ToString().Trim());

        return pages;
    }

    private static string StripHtmlPreserveParagraphs(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        string s = ScriptStyleRegex().Replace(html, string.Empty);
        s = BlockTagRegex().Replace(s, "\n\n");
        s = AnyTagRegex().Replace(s, string.Empty);
        s = System.Net.WebUtility.HtmlDecode(s);
        return MultiNewlineRegex().Replace(s, "\n\n").Trim();
    }

    private static string StripHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        string s = ScriptStyleRegex().Replace(html, " ");
        s = AnyTagRegex().Replace(s, " ");
        s = System.Net.WebUtility.HtmlDecode(s);
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    private static string CleanFileNameTitle(string rawName)
    {
        return rawName.Replace('_', ' ').Trim();
    }

    private static string InferCategoryFromPath(string filePath)
    {
        try
        {
            var parent = Directory.GetParent(filePath);
            if (parent != null && !string.IsNullOrWhiteSpace(parent.Name) && parent.Parent != null)
            {
                return parent.Name;
            }
        }
        catch
        {
            // Ignore
        }
        return "Загальне";
    }

    private static int EstimatePagesByFileSize(string filePath, int bytesPerPage = 3200)
    {
        try
        {
            long len = new FileInfo(filePath).Length;
            return Math.Max(1, (int)(len / bytesPerPage));
        }
        catch
        {
            return 1;
        }
    }

    [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HtmlTitleRegex();

    [GeneratedRegex(@"<meta[^>]+name=[""']author[""'][^>]+content=[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlAuthorRegex();

    [GeneratedRegex(@"<(script|style)[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptStyleRegex();

    [GeneratedRegex(@"<(/p|/div|/h[1-6]|br\s*/?|/li|/tr)[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockTagRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex AnyTagRegex();

    [GeneratedRegex(@"(\r?\n\s*){3,}")]
    private static partial Regex MultiNewlineRegex();
}
