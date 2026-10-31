using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using BookHub.Core.Models;
using BookHub.Core.Parsers;
using BookHub.Core.Pdf;

namespace BookHub.Core.Services;

public static partial class ExportAndBackupService
{
    /// <summary>
    /// Exports all annotations, highlights, and bookmarks for a book into a clean Obsidian-ready Markdown file.
    /// </summary>
    public static void ExportAnnotationsToMarkdown(BookRecord book, IEnumerable<AnnotationRecord> annotations, string targetMdFilePath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"title: \"{book.Title.Replace("\"", "\\\"")}\"");
        sb.AppendLine($"author: \"{book.Author.Replace("\"", "\\\"")}\"");
        sb.AppendLine($"format: {book.FormatBadge}");
        if (!string.IsNullOrWhiteSpace(book.Isbn)) sb.AppendLine($"isbn: \"{book.Isbn}\"");
        if (!string.IsNullOrWhiteSpace(book.Doi)) sb.AppendLine($"doi: \"{book.Doi}\"");
        if (!string.IsNullOrWhiteSpace(book.Series)) sb.AppendLine($"series: \"{book.Series}\"");
        sb.AppendLine($"tags: [{string.Join(", ", book.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))}]");
        sb.AppendLine($"exported_at: {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"# 📚 Конспект та анотації: {book.Title}");
        sb.AppendLine($"**Автор:** {book.Author}  ");
        sb.AppendLine($"**Файл:** `{book.FilePath}`  ");
        sb.AppendLine($"**Прогрес читання:** {book.CurrentPage} / {book.PageCount} стор. ({book.ReadingProgress:0.0}%)");
        sb.AppendLine();

        var list = annotations.OrderBy(a => a.PageNumber).ThenBy(a => a.CreatedAt).ToList();
        if (list.Count == 0)
        {
            sb.AppendLine("> *Для цієї книги ще не створено виділень або нотаток.*");
        }
        else
        {
            sb.AppendLine("## 📝 Витяги, цитати та закладки");
            sb.AppendLine();

            foreach (var ann in list)
            {
                string badge = ann.IsBookmark ? "🔖 Закладка" : $"🖍 Маркер ({ann.HighlightColor})";
                sb.AppendLine($"### Сторінка {ann.PageNumber} — {badge}");
                if (!string.IsNullOrWhiteSpace(ann.QuoteText))
                {
                    foreach (var line in ann.QuoteText.Split('\n'))
                    {
                        sb.AppendLine($"> {line.TrimEnd('\r')}");
                    }
                    sb.AppendLine();
                }

                if (!string.IsNullOrWhiteSpace(ann.CommentText))
                {
                    sb.AppendLine($"**Коментар:** {ann.CommentText}");
                    sb.AppendLine();
                }

                sb.AppendLine($"*Додано: {ann.CreatedAt:dd.MM.yyyy HH:mm}*");
                sb.AppendLine();
                sb.AppendLine("---");
                sb.AppendLine();
            }
        }

        File.WriteAllText(targetMdFilePath, sb.ToString(), new UTF8Encoding(false));
    }

    /// <summary>
    /// Exports a specific range of pages from a PDF or text/FB2/EPUB document into a standalone PDF file.
    /// </summary>
    public static void ExportPagesToPdf(BookRecord book, int startPage, int endPage, string outputPdfPath)
    {
        startPage = Math.Max(1, startPage);
        endPage = Math.Max(startPage, endPage);

        if (book.Extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            ExportPdfPagesViaRasterToPdf(book.FilePath, startPage, endPage, outputPdfPath);
        }
        else
        {
            var (pages, _) = BookFormatParser.LoadDocumentPaginatedContent(book.FilePath);
            int clampedStart = Math.Clamp(startPage, 1, Math.Max(1, pages.Count));
            int clampedEnd = Math.Clamp(endPage, clampedStart, Math.Max(1, pages.Count));
            var selectedPages = pages.Skip(clampedStart - 1).Take(clampedEnd - clampedStart + 1).ToList();
            GeneratePdfFromTextPages(book.Title, book.Author, selectedPages, outputPdfPath);
        }
    }

    private static void ExportPdfPagesViaRasterToPdf(string sourcePdfPath, int startPage, int endPage, string outputPdfPath)
    {
        using var session = new PdfDocumentSession(sourcePdfPath);
        int maxPage = Math.Min(endPage, session.PageCount);
        var jpegStreams = new List<(byte[] JpegBytes, int Width, int Height)>();

        for (int p = startPage; p <= maxPage; p++)
        {
            var rendered = session.RenderPageBgra(p, zoom: 1.25);
            using var bmp = new Bitmap(rendered.Width, rendered.Height, PixelFormat.Format32bppArgb);
            var bmpData = bmp.LockBits(new Rectangle(0, 0, rendered.Width, rendered.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < rendered.Height; y++)
                {
                    IntPtr destRow = IntPtr.Add(bmpData.Scan0, y * bmpData.Stride);
                    System.Runtime.InteropServices.Marshal.Copy(rendered.Pixels, y * rendered.Stride, destRow, Math.Min(rendered.Stride, bmpData.Stride));
                }
            }
            finally
            {
                bmp.UnlockBits(bmpData);
            }

            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Jpeg);
            jpegStreams.Add((ms.ToArray(), rendered.Width, rendered.Height));
        }

        WriteJpegPagesToPdf(outputPdfPath, jpegStreams);
    }

    private static void GeneratePdfFromTextPages(string title, string author, List<string> textPages, string outputPdfPath)
    {
        var jpegStreams = new List<(byte[] JpegBytes, int Width, int Height)>();
        int width = 820;
        int height = 1160;

        foreach (var pageText in textPages)
        {
            using var bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                using var headerFont = new Font("Segoe UI", 10f, FontStyle.Bold);
                using var bodyFont = new Font("Segoe UI", 11.5f, FontStyle.Regular);
                using var headerBrush = new SolidBrush(Color.FromArgb(80, 90, 110));
                using var bodyBrush = new SolidBrush(Color.FromArgb(20, 24, 32));

                g.DrawString($"{title} — {author}", headerFont, headerBrush, new RectangleF(48, 32, width - 96, 28));
                g.DrawString(pageText, bodyFont, bodyBrush, new RectangleF(48, 72, width - 96, height - 120));
            }

            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Jpeg);
            jpegStreams.Add((ms.ToArray(), width, height));
        }

        WriteJpegPagesToPdf(outputPdfPath, jpegStreams);
    }

    private static void WriteJpegPagesToPdf(string outputPdfPath, List<(byte[] JpegBytes, int Width, int Height)> pages)
    {
        using var fs = new FileStream(outputPdfPath, FileMode.Create, FileAccess.Write);
        var offsets = new Dictionary<int, long>();

        void WriteAscii(string s)
        {
            var b = Encoding.ASCII.GetBytes(s);
            fs.Write(b, 0, b.Length);
        }

        WriteAscii("%PDF-1.7\n%\xE2\xE3\xCF\xD3\n");

        int pageCount = pages.Count;
        int totalObjects = 2 + pageCount * 3;

        offsets[1] = fs.Position;
        WriteAscii("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        offsets[2] = fs.Position;
        var kids = new StringBuilder();
        for (int i = 0; i < pageCount; i++)
            kids.Append(3 + i * 3).Append(" 0 R ");
        WriteAscii($"2 0 obj\n<< /Type /Pages /Count {pageCount} /Kids [ {kids}] >>\nendobj\n");

        for (int i = 0; i < pageCount; i++)
        {
            int pageObj = 3 + i * 3;
            int contentObj = pageObj + 1;
            int imageObj = pageObj + 2;
            var (jpegBytes, w, h) = pages[i];

            offsets[pageObj] = fs.Position;
            WriteAscii($"{pageObj} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /XObject << /Im0 {imageObj} 0 R >> >> /Contents {contentObj} 0 R >>\nendobj\n");

            string cmd = "q 612 0 0 792 0 0 cm /Im0 Do Q\n";
            offsets[contentObj] = fs.Position;
            WriteAscii($"{contentObj} 0 obj\n<< /Length {cmd.Length} >>\nstream\n{cmd}endstream\nendobj\n");

            offsets[imageObj] = fs.Position;
            WriteAscii($"{imageObj} 0 obj\n<< /Type /XObject /Subtype /Image /Width {w} /Height {h} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {jpegBytes.Length} >>\nstream\n");
            fs.Write(jpegBytes, 0, jpegBytes.Length);
            WriteAscii("\nendstream\nendobj\n");
        }

        long xrefPos = fs.Position;
        WriteAscii($"xref\n0 {totalObjects + 1}\n0000000000 65535 f \n");
        for (int id = 1; id <= totalObjects; id++)
            WriteAscii($"{offsets[id]:0000000000} 00000 n \n");

        WriteAscii($"trailer\n<< /Size {totalObjects + 1} /Root 1 0 R >>\nstartxref\n{xrefPos}\n%%EOF\n");
    }
}
