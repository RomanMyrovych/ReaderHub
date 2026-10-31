using System.IO;
using System.IO.Compression;
using System.Text;

namespace BookHub.Core.Services;

public static class SampleLibraryGenerator
{
    public static void EnsureDemoBooksExist(string sampleRootDir)
    {
        Directory.CreateDirectory(sampleRootDir);

        var pdfDir = Path.Combine(sampleRootDir, "Архітектура та .NET");
        var fb2Dir = Path.Combine(sampleRootDir, "Художня та Класика");
        var docsDir = Path.Combine(sampleRootDir, "Документація");

        Directory.CreateDirectory(pdfDir);
        Directory.CreateDirectory(fb2Dir);
        Directory.CreateDirectory(docsDir);

        string heavyPdfPath = Path.Combine(pdfDir, "Pro_NET_9_High_Performance_Architecture_1250p.pdf");
        if (!File.Exists(heavyPdfPath))
        {
            GenerateMultiPagePdfWithToc(heavyPdfPath, pageCount: 1250,
                title: "Pro .NET 9 High-Performance Architecture (1250 Pages)",
                author: "Олександр Коваленко, Мартін Фаулер");
        }

        string secondPdfPath = Path.Combine(pdfDir, "WPF_MVVM_Internals_And_PDFium_Rendering.pdf");
        if (!File.Exists(secondPdfPath))
        {
            GenerateMultiPagePdfWithToc(secondPdfPath, pageCount: 420,
                title: "WPF & PDFium: Нативний рендеринг і віртуалізація",
                author: "Андрій Мельник");
        }

        string fb2Path = Path.Combine(fb2Dir, "Kiberpank_Хроніки_Нейромереж.fb2");
        if (!File.Exists(fb2Path))
        {
            GenerateSampleFb2WithCover(fb2Path);
        }

        string docxPath = Path.Combine(docsDir, "Технічна_Специфікація_BookHub_Архітектура.docx");
        if (!File.Exists(docxPath))
        {
            GenerateSampleDocx(docxPath);
        }

        string mdPath = Path.Combine(docsDir, "Посібник_Користувача_BookHub.md");
        if (!File.Exists(mdPath))
        {
            File.WriteAllText(mdPath, """
                # Посібник користувача BookHub (.NET 9 WPF)

                Ласкаво просимо до **BookHub** — швидкого каталогізатора та потокового рідера великих електронних книг і технічної документації.

                ## Ключові можливості

                1. **Миттєве відкриття PDF на 1200+ сторінок (< 0.5 сек)**:
                   Використовується нативний рушій PDFium із потоковим доступом `FPDF_LoadCustomDocument`. У пам'яті тримаються лише поточна сторінка та сусідні (`+1 / -1`) сторінки у фоновому потоці.

                2. **Потоковий FB2 парсер (`XmlReader`)**:
                   Читає метадані, авторів, жанри, обкладинку у форматі `binary base64` та розбиває текст на віртуальні сторінки без завантаження всього DOM-дерева.

                3. **Віртуалізована сітка обкладинок (`VirtualizingWrapPanel`)**:
                   Забезпечує плавний скролінг каталогу при тисячах книг без зависання інтерфейсу.

                4. **Збереження прогресу читання**:
                   При закритті вікна рідера автоматично оновлюється поточна сторінка, відсоток прочитаного та статус книги в SQLite.
                """, Encoding.UTF8);
        }
    }

    /// <summary>
    /// Generates a valid 1250-page PDF with PDF Outlines (Bookmarks / TOC) and page headers in ~80ms.
    /// </summary>
    public static void GenerateMultiPagePdfWithToc(string filePath, int pageCount, string title, string author)
    {
        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
        var offsets = new Dictionary<int, long>();

        void WriteAscii(string text)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(text);
            fs.Write(bytes, 0, bytes.Length);
        }

        WriteAscii("%PDF-1.7\n%\xE2\xE3\xCF\xD3\n");

        // Object IDs:
        // 1: Catalog
        // 2: Pages root
        // 3: Font Helvetica
        // 4: Font Helvetica-Bold
        // 5: Info dictionary
        // 6: Outlines root
        // 7..18: 12 Chapter outline items
        int chapterCount = 12;
        int firstPageObjId = 7 + chapterCount; // 19
        // For each page i (0..pageCount-1):
        // Page object ID = firstPageObjId + i * 2
        // Content stream ID = firstPageObjId + i * 2 + 1

        int totalObjects = firstPageObjId + pageCount * 2 - 1;

        // 1: Catalog
        offsets[1] = fs.Position;
        WriteAscii("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /Outlines 6 0 R /PageMode /UseOutlines >>\nendobj\n");

        // 2: Pages Root
        offsets[2] = fs.Position;
        var kidsSb = new StringBuilder();
        for (int i = 0; i < pageCount; i++)
        {
            kidsSb.Append(firstPageObjId + i * 2).Append(" 0 R ");
        }
        WriteAscii($"2 0 obj\n<< /Type /Pages /Count {pageCount} /Kids [ {kidsSb}] >>\nendobj\n");

        // 3 & 4: Fonts
        offsets[3] = fs.Position;
        WriteAscii("3 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        offsets[4] = fs.Position;
        WriteAscii("4 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>\nendobj\n");

        // 5: Info (UTF-16BE hex string for Unicode title & author)
        offsets[5] = fs.Position;
        string titleHex = ToPdfUtf16BeHex(title);
        string authorHex = ToPdfUtf16BeHex(author);
        WriteAscii($"5 0 obj\n<< /Title <{titleHex}> /Author <{authorHex}> /Producer (BookHub PDFium Engine) >>\nendobj\n");

        // 6: Outlines Root
        offsets[6] = fs.Position;
        int firstOutlineId = 7;
        int lastOutlineId = 7 + chapterCount - 1;
        WriteAscii($"6 0 obj\n<< /Type /Outlines /First {firstOutlineId} 0 R /Last {lastOutlineId} 0 R /Count {chapterCount} >>\nendobj\n");

        // 7..18: Chapter Bookmarks
        int pagesPerChapter = Math.Max(1, pageCount / chapterCount);
        for (int c = 0; c < chapterCount; c++)
        {
            int objId = firstOutlineId + c;
            int targetPageIdx = Math.Min(pageCount - 1, c * pagesPerChapter);
            int targetPageObjId = firstPageObjId + targetPageIdx * 2;
            string chapTitleHex = ToPdfUtf16BeHex($"Розділ {c + 1}. Архітектура та оптимізація (стор. {targetPageIdx + 1})");

            string prevPart = c > 0 ? $"/Prev {objId - 1} 0 R " : "";
            string nextPart = c < chapterCount - 1 ? $"/Next {objId + 1} 0 R " : "";

            offsets[objId] = fs.Position;
            WriteAscii($"{objId} 0 obj\n<< /Title <{chapTitleHex}> /Parent 6 0 R {prevPart}{nextPart}/Dest [{targetPageObjId} 0 R /XYZ 0 792 0] >>\nendobj\n");
        }

        // Pages & Content Streams
        for (int i = 0; i < pageCount; i++)
        {
            int pageObjId = firstPageObjId + i * 2;
            int contentObjId = pageObjId + 1;
            int pageNum = i + 1;
            int chapterNum = Math.Min(chapterCount, (i / pagesPerChapter) + 1);

            string streamContent =
                "q\n" +
                "0.11 0.14 0.22 rg\n" +
                "0 730 612 62 re f\n" +
                "0.23 0.51 0.96 rg\n" +
                "0 726 612 4 re f\n" +
                "BT\n" +
                "/F2 16 Tf\n" +
                "1 1 1 rg\n" +
                $"36 754 Td (BOOKHUB TECHNICAL LIBRARY - CHAPTER {chapterNum}) Tj\n" +
                "ET\n" +
                "BT\n" +
                "/F2 22 Tf\n" +
                "0.12 0.15 0.20 rg\n" +
                $"36 675 Td (Page {pageNum} of {pageCount}) Tj\n" +
                "/F1 12 Tf\n" +
                "0 -32 Td (High-Performance Streaming PDF Rendering via Native PDFium) Tj\n" +
                "0 -22 Td (Only visible page and +1/-1 neighbor pages are rendered in background.) Tj\n" +
                "0 -28 Td (Technical Specification & Memory Profile:) Tj\n" +
                $"0 -20 Td (- Document Total Pages: {pageCount} pages) Tj\n" +
                $"0 -18 Td (- Current Active Section: Chapter {chapterNum} (Page {pageNum})) Tj\n" +
                "0 -18 Td (- Zero UI Blocking: Asynchronous Virtualized Page Pipeline) Tj\n" +
                "0 -18 Td (- Custom FileStream Block Reader: Instant < 50ms open time) Tj\n" +
                "ET\n" +
                "0.93 0.95 0.98 rg\n" +
                "36 320 540 160 re f\n" +
                "0.23 0.51 0.96 RG 1.5 w\n" +
                "36 320 540 160 re S\n" +
                "BT\n" +
                "/F2 13 Tf\n" +
                "0.15 0.25 0.45 rg\n" +
                $"54 450 Td (Architecture Diagram & Code Listing #{pageNum}.1) Tj\n" +
                "/F1 11 Tf\n" +
                "0.2 0.25 0.3 rg\n" +
                "0 -24 Td (public async Task<BitmapSource> RenderPageAsync(int pageNumber, double zoom)) Tj\n" +
                "0 -18 Td ({) Tj\n" +
                "0 -18 Td (    // Renders only current page + preloads adjacent pages (+1 / -1)) Tj\n" +
                "0 -18 Td (    return await Task.Run(() => _pdfSession.RenderPageBgra(pageNumber, zoom));) Tj\n" +
                "0 -18 Td (}) Tj\n" +
                "ET\n" +
                "BT\n" +
                "/F1 10 Tf\n" +
                "0.45 0.5 0.55 rg\n" +
                $"250 36 Td (- Page {pageNum} / {pageCount} -) Tj\n" +
                "ET\n" +
                "Q\n";

            offsets[pageObjId] = fs.Position;
            WriteAscii($"{pageObjId} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {contentObjId} 0 R >>\nendobj\n");

            offsets[contentObjId] = fs.Position;
            int byteLen = Encoding.ASCII.GetByteCount(streamContent);
            WriteAscii($"{contentObjId} 0 obj\n<< /Length {byteLen} >>\nstream\n{streamContent}\nendstream\nendobj\n");
        }

        long xrefPos = fs.Position;
        WriteAscii($"xref\n0 {totalObjects + 1}\n0000000000 65535 f \n");
        for (int id = 1; id <= totalObjects; id++)
        {
            WriteAscii($"{offsets[id]:0000000000} 00000 n \n");
        }

        WriteAscii($"trailer\n<< /Size {totalObjects + 1} /Root 1 0 R /Info 5 0 R >>\nstartxref\n{xrefPos}\n%%EOF\n");
    }

    private static string ToPdfUtf16BeHex(string text)
    {
        byte[] utf16Be = Encoding.BigEndianUnicode.GetBytes(text);
        return "FEFF" + Convert.ToHexString(utf16Be);
    }

    private static void GenerateSampleFb2WithCover(string filePath)
    {
        // Create a small stylized PNG cover in base64 for testing FB2 binary cover extraction
        using var bmp = new System.Drawing.Bitmap(240, 340);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.Clear(System.Drawing.Color.FromArgb(28, 22, 48));
            using var brush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(168, 85, 247));
            g.FillRectangle(brush, 0, 0, 240, 14);
            using var font = new System.Drawing.Font("Segoe UI", 16, System.Drawing.FontStyle.Bold);
            g.DrawString("КІБЕРПАНК\nХРОНІКИ\nНЕЙРОМЕРЕЖ", font, System.Drawing.Brushes.White, new System.Drawing.RectangleF(20, 50, 200, 160));
            using var subFont = new System.Drawing.Font("Segoe UI", 11, System.Drawing.FontStyle.Regular);
            g.DrawString("Сергій Вітер • FB2 Edition", subFont, System.Drawing.Brushes.LightGray, new System.Drawing.PointF(20, 280));
        }
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        string base64Cover = Convert.ToBase64String(ms.ToArray());

        var sb = new StringBuilder();
        sb.AppendLine("""<?xml version="1.0" encoding="utf-8"?>""");
        sb.AppendLine("""<FictionBook xmlns="http://www.gribuser.ru/xml/fictionbook/2.0" xmlns:l="http://www.w3.org/1999/xlink">""");
        sb.AppendLine("""  <description>""");
        sb.AppendLine("""    <title-info>""");
        sb.AppendLine("""      <genre>sf_cyberpunk</genre>""");
        sb.AppendLine("""      <genre>computers</genre>""");
        sb.AppendLine("""      <author><first-name>Сергій</first-name><last-name>Вітер</last-name></author>""");
        sb.AppendLine("""      <book-title>Кіберпанк: Хроніки Нейромереж</book-title>""");
        sb.AppendLine("""      <annotation><p>Науково-фантастичний роман та технічні нотатки про високошвидкісні потокові системи, розподілений штучний інтелект та архітектуру майбутнього.</p></annotation>""");
        sb.AppendLine("""      <coverpage><image l:href="#cover.png"/></coverpage>""");
        sb.AppendLine("""      <lang>uk</lang>""");
        sb.AppendLine("""    </title-info>""");
        sb.AppendLine("""  </description>""");
        sb.AppendLine("""  <body>""");

        for (int ch = 1; ch <= 15; ch++)
        {
            sb.AppendLine($"    <section><title><p>Розділ {ch}. Протокол квантової синхронізації</p></title>");
            for (int p = 1; p <= 18; p++)
            {
                sb.AppendLine($"      <p>Абзац {ch}.{p}. У нічному Києві 2089 року сервери обробляли мільярди транзакцій щосекунди. Завдяки потоковому читанню через XmlReader та відсутності блокування інтерфейсу, навіть гігантські архіви даних відкривалися за долі мілісекунди. Інженери перевіряли цілісність хеш-сум і миттєво знаходили потрібні фрагменти пам'яті.</p>");
            }
            sb.AppendLine("    </section>");
        }

        sb.AppendLine("""  </body>""");
        sb.AppendLine($"""  <binary id="cover.png" content-type="image/png">{base64Cover}</binary>""");
        sb.AppendLine("""</FictionBook>""");

        File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
    }

    private static void GenerateSampleDocx(string filePath)
    {
        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
        using var archive = new ZipArchive(fs, ZipArchiveMode.Create);

        var coreEntry = archive.CreateEntry("docProps/core.xml");
        using (var sw = new StreamWriter(coreEntry.Open(), Encoding.UTF8))
        {
            sw.Write("""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties"
                                   xmlns:dc="http://purl.org/dc/elements/1.1/">
                  <dc:title>Технічна специфікація архітектури BookHub</dc:title>
                  <dc:creator>Відділ системної архітектури</dc:creator>
                  <dc:description>Опис потокового рендерингу PDFium, кешування обкладинок та SQLite бази даних.</dc:description>
                </cp:coreProperties>
                """);
        }

        var appEntry = archive.CreateEntry("docProps/app.xml");
        using (var sw = new StreamWriter(appEntry.Open(), Encoding.UTF8))
        {
            sw.Write("""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Properties xmlns="http://schemas.openxmlformats.org/officeDocument/2006/extended-properties">
                  <Pages>14</Pages>
                </Properties>
                """);
        }

        var docEntry = archive.CreateEntry("word/document.xml");
        using (var sw = new StreamWriter(docEntry.Open(), Encoding.UTF8))
        {
            var sb = new StringBuilder();
            sb.Append("""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
                  <w:body>
                """);
            for (int s = 1; s <= 6; s++)
            {
                sb.Append($"""
                    <w:p>
                      <w:pPr><w:pStyle w:val="Heading1"/></w:pPr>
                      <w:r><w:t>{s}. Модуль високошвидкісної обробки документів ( секція {s} )</w:t></w:r>
                    </w:p>
                    """);
                for (int p = 1; p <= 8; p++)
                {
                    sb.Append($"""
                        <w:p>
                          <w:r><w:t>Пункт {s}.{p}. Система BookHub гарантує, що жоден файл на диску не перейменовується і не копіюється. У базі даних SQLite зберігаються лише шляхи до оригіналів, хеш перших мегабайтів для дедуплікації, а також прогрес читання користувача.</w:t></w:r>
                        </w:p>
                        """);
                }
            }
            sb.Append("""
                  </w:body>
                </w:document>
                """);
            sw.Write(sb.ToString());
        }
    }
}
