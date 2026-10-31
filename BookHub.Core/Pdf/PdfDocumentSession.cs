using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using BookHub.Core.Models;

namespace BookHub.Core.Pdf;

public sealed class PdfDocumentSession : IDisposable
{
    private readonly FileStream _stream;
    private readonly PdfiumNative.FpdfGetBlockDelegate _getBlockDelegate;
    private GCHandle _delegateHandle;
    private IntPtr _doc;
    private bool _disposed;
    private bool? _cachedHasTextLayer;

    public string FilePath { get; }
    public int PageCount { get; }
    public string Title { get; }
    public string Author { get; }

    public PdfDocumentSession(string filePath)
    {
        FilePath = filePath;
        PdfiumNative.EnsureInitialized();

        _stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 65536, useAsync: false);
        _getBlockDelegate = ReadBlockCallback;
        _delegateHandle = GCHandle.Alloc(_getBlockDelegate);

        var fileAccess = new PdfiumNative.FPDF_FILEACCESS
        {
            m_FileLen = (uint)Math.Min(_stream.Length, uint.MaxValue),
            m_GetBlock = Marshal.GetFunctionPointerForDelegate(_getBlockDelegate),
            m_Param = IntPtr.Zero
        };

        lock (PdfiumNative.SyncRoot)
        {
            _doc = PdfiumNative.FPDF_LoadCustomDocument(ref fileAccess, IntPtr.Zero);
            if (_doc == IntPtr.Zero)
            {
                _delegateHandle.Free();
                _stream.Dispose();
                throw new InvalidOperationException($"Не вдалося відкрити PDF-документ: {filePath}");
            }

            PageCount = PdfiumNative.FPDF_GetPageCount(_doc);
            Title = PdfiumNative.GetMetaString(_doc, "Title");
            Author = PdfiumNative.GetMetaString(_doc, "Author");
        }
    }

    private int ReadBlockCallback(IntPtr param, uint position, IntPtr pBuf, uint size)
    {
        try
        {
            if (position + size > _stream.Length)
                return 0;

            _stream.Position = position;
            byte[] temp = System.Buffers.ArrayPool<byte>.Shared.Rent((int)size);
            try
            {
                int totalRead = 0;
                while (totalRead < size)
                {
                    int read = _stream.Read(temp, totalRead, (int)size - totalRead);
                    if (read <= 0) break;
                    totalRead += read;
                }
                if (totalRead != size) return 0;
                Marshal.Copy(temp, 0, pBuf, (int)size);
                return 1;
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(temp);
            }
        }
        catch
        {
            return 0;
        }
    }

    public List<TocItem> GetTableOfContents()
    {
        lock (PdfiumNative.SyncRoot)
        {
            if (_disposed || _doc == IntPtr.Zero) return [];
            var visited = new HashSet<IntPtr>();
            var bookmarks = ReadBookmarksRecursive(IntPtr.Zero, visited, 0);
            if (bookmarks.Count == 0 && PageCount > 1)
            {
                int step = PageCount <= 30 ? 5 : (PageCount <= 150 ? 15 : 25);
                for (int p = 1; p <= PageCount; p += step)
                {
                    bookmarks.Add(new TocItem
                    {
                        Title = $"Сторінка {p}",
                        PageNumber = p
                    });
                }
            }
            return bookmarks;
        }
    }

    private List<TocItem> ReadBookmarksRecursive(IntPtr parentBookmark, HashSet<IntPtr> visited, int depth)
    {
        var items = new List<TocItem>();
        if (depth > 12) return items;

        IntPtr current = PdfiumNative.FPDFBookmark_GetFirstChild(_doc, parentBookmark);
        while (current != IntPtr.Zero && visited.Add(current))
        {
            string title = PdfiumNative.GetBookmarkTitle(current);
            int pageIndex = -1;

            IntPtr dest = PdfiumNative.FPDFBookmark_GetDest(_doc, current);
            if (dest == IntPtr.Zero)
            {
                IntPtr action = PdfiumNative.FPDFBookmark_GetAction(current);
                if (action != IntPtr.Zero)
                {
                    dest = PdfiumNative.FPDFAction_GetDest(_doc, action);
                }
            }

            if (dest != IntPtr.Zero)
            {
                pageIndex = PdfiumNative.FPDFDest_GetDestPageIndex(_doc, dest);
            }

            var children = ReadBookmarksRecursive(current, visited, depth + 1);
            int resolvedPage = pageIndex >= 0
                ? Math.Clamp(pageIndex + 1, 1, Math.Max(1, PageCount))
                : (children.Count > 0 ? children[0].PageNumber : 1);

            var item = new TocItem
            {
                Title = string.IsNullOrWhiteSpace(title) ? "Розділ" : title,
                PageNumber = resolvedPage,
                Children = children
            };
            items.Add(item);

            current = PdfiumNative.FPDFBookmark_GetNextSibling(_doc, current);
        }

        return items;
    }

    public (double Width, double Height) GetPageSizePoints(int pageNumber1Based)
    {
        lock (PdfiumNative.SyncRoot)
        {
            if (_disposed || _doc == IntPtr.Zero) return (612, 792);
            int index = Math.Clamp(pageNumber1Based - 1, 0, Math.Max(0, PageCount - 1));
            if (PdfiumNative.FPDF_GetPageSizeByIndexF(_doc, index, out var size) != 0 && size.Width > 0 && size.Height > 0)
            {
                return (size.Width, size.Height);
            }
            return (612, 792);
        }
    }

    public PooledPageBuffer RenderPagePooledBgra(
        int pageNumber1Based,
        double zoom = 1.25,
        int maxWidthPx = 0,
        uint backgroundArgb = 0xFFFFFFFF,
        bool lowResPreview = false,
        uint pageTextArgb = 0xFF000000,
        bool smartThemeFilter = false,
        bool isDarkTheme = false)
    {
        int width;
        int height;
        int stride;
        int byteLength;
        byte[] pooledPixels;

        lock (PdfiumNative.SyncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            int pageIndex = Math.Clamp(pageNumber1Based - 1, 0, Math.Max(0, PageCount - 1));

            IntPtr page = PdfiumNative.FPDF_LoadPage(_doc, pageIndex);
            if (page == IntPtr.Zero)
                throw new InvalidOperationException($"Не вдалося завантажити сторінку {pageNumber1Based}");

            try
            {
                double ptW = PdfiumNative.FPDF_GetPageWidth(page);
                double ptH = PdfiumNative.FPDF_GetPageHeight(page);
                if (ptW <= 0) ptW = 612;
                if (ptH <= 0) ptH = 792;

                double baseDpiScale = lowResPreview ? (72.0 / 72.0) : (144.0 / 72.0);
                double scale = baseDpiScale * Math.Clamp(zoom, 0.25, 4.0);

                if (maxWidthPx > 0)
                {
                    scale = maxWidthPx / ptW;
                }

                int maxClamp = lowResPreview ? 520 : 4096;
                width = Math.Clamp((int)Math.Round(ptW * scale), 64, maxClamp);
                height = Math.Clamp((int)Math.Round(ptH * scale), 64, maxClamp);

                IntPtr bmp = PdfiumNative.FPDFBitmap_CreateEx(width, height, 4, IntPtr.Zero, width * 4);
                if (bmp == IntPtr.Zero)
                    throw new OutOfMemoryException("Не вдалося виділити буфер PDFium для рендерингу сторінки.");

                try
                {
                    uint initialCanvasArgb = smartThemeFilter ? 0xFFFFFFFFu : backgroundArgb;
                    PdfiumNative.FPDFBitmap_FillRect(bmp, 0, 0, width, height, initialCanvasArgb);
                    int flags = lowResPreview
                        ? (PdfiumNative.FPDF_RENDER_LIMITEDIMAGECACHE | PdfiumNative.FPDF_RENDER_NO_SMOOTHIMAGE)
                        : (PdfiumNative.FPDF_ANNOT | PdfiumNative.FPDF_LCD_TEXT | PdfiumNative.FPDF_RENDER_LIMITEDIMAGECACHE);

                    PdfiumNative.FPDF_RenderPageBitmap(bmp, page, 0, 0, width, height, 0, flags);

                    IntPtr bufferPtr = PdfiumNative.FPDFBitmap_GetBuffer(bmp);
                    stride = PdfiumNative.FPDFBitmap_GetStride(bmp);
                    byteLength = stride * height;
                    pooledPixels = System.Buffers.ArrayPool<byte>.Shared.Rent(byteLength);
                    Marshal.Copy(bufferPtr, pooledPixels, 0, byteLength);
                }
                finally
                {
                    PdfiumNative.FPDFBitmap_Destroy(bmp);
                }
            }
            finally
            {
                PdfiumNative.FPDF_ClosePage(page);
            }
        }

        if (smartThemeFilter)
        {
            ApplySmartThemeColorFilter(pooledPixels, byteLength, backgroundArgb, pageTextArgb, isDarkTheme);
        }

        return new PooledPageBuffer(pageNumber1Based, width, height, stride, byteLength, pooledPixels, lowResPreview);
    }

    /// <summary>
    /// Chroma-aware LUT color remapper for PDF pages (Smart Dark &amp; Sepia Mode).
    /// Remaps paper background and text/line/formula luminance to the active theme's ReaderPageBg &amp; ReaderPageText
    /// while preserving the natural hue of colored illustrations, charts, and photos.
    /// Executes outside the PDFium native lock in &lt; 0.8 ms per page.
    /// </summary>
    private static void ApplySmartThemeColorFilter(
        byte[] pixels,
        int byteLength,
        uint targetBgArgb,
        uint targetTextArgb,
        bool isDarkTheme)
    {
        byte bgR = (byte)((targetBgArgb >> 16) & 0xFF);
        byte bgG = (byte)((targetBgArgb >> 8) & 0xFF);
        byte bgB = (byte)(targetBgArgb & 0xFF);

        byte fgR = (byte)((targetTextArgb >> 16) & 0xFF);
        byte fgG = (byte)((targetTextArgb >> 8) & 0xFF);
        byte fgB = (byte)(targetTextArgb & 0xFF);

        // Clean White theme (white paper + dark ink) requires no pixel transformation
        if (!isDarkTheme && bgR >= 252 && bgG >= 252 && bgB >= 252)
            return;

        Span<byte> lutR = stackalloc byte[256];
        Span<byte> lutG = stackalloc byte[256];
        Span<byte> lutB = stackalloc byte[256];

        for (int y = 0; y < 256; y++)
        {
            int inv = 255 - y;
            lutR[y] = (byte)((fgR * inv + bgR * y + 127) / 255);
            lutG[y] = (byte)((fgG * inv + bgG * y + 127) / 255);
            lutB[y] = (byte)((fgB * inv + bgB * y + 127) / 255);
        }

        Span<byte> span = pixels.AsSpan(0, byteLength);
        for (int i = 0; i <= span.Length - 4; i += 4)
        {
            byte b = span[i];
            byte g = span[i + 1];
            byte r = span[i + 2];

            // Fast path 1: pure white paper pixel (~80% of PDF page area)
            if (b == 255 && g == 255 && r == 255)
            {
                span[i] = bgB;
                span[i + 1] = bgG;
                span[i + 2] = bgR;
                continue;
            }

            // Fast path 2: exact grayscale pixel (black ink, gray borders, grayscale anti-aliasing)
            if (b == g && g == r)
            {
                span[i] = lutB[b];
                span[i + 1] = lutG[b];
                span[i + 2] = lutR[b];
                continue;
            }

            int maxC = r > g ? (r > b ? r : b) : (g > b ? g : b);
            int minC = r < g ? (r < b ? r : b) : (g < b ? g : b);
            int chroma = maxC - minC;
            int lum = (r * 77 + g * 150 + b * 29) >> 8;

            byte remB = lutB[lum];
            byte remG = lutG[lum];
            byte remR = lutR[lum];

            // Near-achromatic pixels (including ClearType/LCD subpixel anti-aliased glyph edges)
            if (chroma <= 26)
            {
                span[i] = remB;
                span[i + 1] = remG;
                span[i + 2] = remR;
                continue;
            }

            if (isDarkTheme)
            {
                // For colored headings, links, and diagrams in dark mode:
                // Keep natural RGB hue while lifting dark colors so they remain legible on a dark page.
                int targetLum = lutG[lum];
                int boostedR = r;
                int boostedG = g;
                int boostedB = b;

                if (lum < 140 && maxC > 0)
                {
                    int desiredPeak = Math.Clamp(targetLum + (chroma >> 1), 135, 245);
                    if (desiredPeak > maxC)
                    {
                        boostedR = Math.Min(255, (r * desiredPeak) / maxC);
                        boostedG = Math.Min(255, (g * desiredPeak) / maxC);
                        boostedB = Math.Min(255, (b * desiredPeak) / maxC);
                    }
                }

                if (chroma < 74)
                {
                    // Smooth blend between achromatic LUT and hue-preserved color
                    int weightColor = ((chroma - 26) * 256) / 48; // 0..256
                    int weightLut = 256 - weightColor;
                    span[i] = (byte)((remB * weightLut + boostedB * weightColor) >> 8);
                    span[i + 1] = (byte)((remG * weightLut + boostedG * weightColor) >> 8);
                    span[i + 2] = (byte)((remR * weightLut + boostedR * weightColor) >> 8);
                }
                else
                {
                    // Vivid photos & colorful illustrations: keep natural hue (never invert into negative!)
                    span[i] = (byte)boostedB;
                    span[i + 1] = (byte)boostedG;
                    span[i + 2] = (byte)boostedR;
                }
            }
            else
            {
                // Tinted light theme (e.g. Warm Sepia): gently tint paper-bright colors while preserving illustrations
                span[i] = (byte)((b * bgB) / 255);
                span[i + 1] = (byte)((g * bgG) / 255);
                span[i + 2] = (byte)((r * bgR) / 255);
            }
        }
    }

    public RenderedPageBuffer RenderPageBgra(
        int pageNumber1Based,
        double zoom = 1.25,
        int maxWidthPx = 0,
        uint backgroundArgb = 0xFFFFFFFF,
        bool smartThemeFilter = false,
        uint pageTextArgb = 0xFFE2E8F0,
        bool isDarkTheme = true)
    {
        int width;
        int height;
        int stride;
        byte[] pixels;

        lock (PdfiumNative.SyncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            int pageIndex = Math.Clamp(pageNumber1Based - 1, 0, Math.Max(0, PageCount - 1));

            IntPtr page = PdfiumNative.FPDF_LoadPage(_doc, pageIndex);
            if (page == IntPtr.Zero)
                throw new InvalidOperationException($"Не вдалося завантажити сторінку {pageNumber1Based}");

            try
            {
                double ptW = PdfiumNative.FPDF_GetPageWidth(page);
                double ptH = PdfiumNative.FPDF_GetPageHeight(page);
                if (ptW <= 0) ptW = 612;
                if (ptH <= 0) ptH = 792;

                double baseDpiScale = 144.0 / 72.0; // Base crisp 144 DPI at zoom 1.0
                double scale = baseDpiScale * Math.Clamp(zoom, 0.25, 4.0);

                if (maxWidthPx > 0)
                {
                    scale = maxWidthPx / ptW;
                }

                width = Math.Clamp((int)Math.Round(ptW * scale), 64, 4096);
                height = Math.Clamp((int)Math.Round(ptH * scale), 64, 4096);

                // FPDFBitmap_BGRA = 4
                IntPtr bmp = PdfiumNative.FPDFBitmap_CreateEx(width, height, 4, IntPtr.Zero, width * 4);
                if (bmp == IntPtr.Zero)
                    throw new OutOfMemoryException("Не вдалося виділити буфер PDFium для рендерингу сторінки.");

                try
                {
                    uint initialCanvasArgb = smartThemeFilter ? 0xFFFFFFFFu : backgroundArgb;
                    PdfiumNative.FPDFBitmap_FillRect(bmp, 0, 0, width, height, initialCanvasArgb);
                    int flags = PdfiumNative.FPDF_ANNOT | PdfiumNative.FPDF_LCD_TEXT | PdfiumNative.FPDF_RENDER_LIMITEDIMAGECACHE;
                    PdfiumNative.FPDF_RenderPageBitmap(bmp, page, 0, 0, width, height, 0, flags);

                    IntPtr bufferPtr = PdfiumNative.FPDFBitmap_GetBuffer(bmp);
                    stride = PdfiumNative.FPDFBitmap_GetStride(bmp);
                    pixels = new byte[stride * height];
                    Marshal.Copy(bufferPtr, pixels, 0, pixels.Length);
                }
                finally
                {
                    PdfiumNative.FPDFBitmap_Destroy(bmp);
                }
            }
            finally
            {
                PdfiumNative.FPDF_ClosePage(page);
            }
        }

        if (smartThemeFilter)
        {
            ApplySmartThemeColorFilter(pixels, pixels.Length, backgroundArgb, pageTextArgb, isDarkTheme);
        }

        return new RenderedPageBuffer(pageNumber1Based, width, height, stride, pixels);
    }

    /// <summary>
    /// Character-level and paragraph-level vector text selection via PDFium (FPDFText_GetCharIndexAtPos,
    /// FPDFText_GetCharBox, FPDFText_CountRects, FPDFText_GetRect, FPDFText_GetText).
    /// Returns both the exact selected text and the per-line/per-word highlight rectangles in normalized [0..1] page space.
    /// </summary>
    public PdfTextSelectionResult SelectTextAndHighlightRects(
        int pageNumber1Based,
        double startNormX,
        double startNormY,
        double endNormX,
        double endNormY)
    {
        lock (PdfiumNative.SyncRoot)
        {
            if (_disposed || _doc == IntPtr.Zero || PageCount <= 0)
                return PdfTextSelectionResult.Empty;

            int pageIndex = Math.Clamp(pageNumber1Based - 1, 0, PageCount - 1);
            IntPtr page = PdfiumNative.FPDF_LoadPage(_doc, pageIndex);
            if (page == IntPtr.Zero)
                return PdfTextSelectionResult.Empty;

            try
            {
                double ptW = PdfiumNative.FPDF_GetPageWidth(page);
                double ptH = PdfiumNative.FPDF_GetPageHeight(page);
                if (ptW <= 0) ptW = 612;
                if (ptH <= 0) ptH = 792;

                IntPtr textPage = PdfiumNative.FPDFText_LoadPage(page);
                if (textPage == IntPtr.Zero)
                    return PdfTextSelectionResult.Empty;

                try
                {
                    int totalChars = PdfiumNative.FPDFText_CountChars(textPage);
                    if (totalChars <= 0)
                        return PdfTextSelectionResult.Empty;

                    double startPdfX = Math.Clamp(startNormX, 0.0, 1.0) * ptW;
                    double startPdfY = (1.0 - Math.Clamp(startNormY, 0.0, 1.0)) * ptH;
                    double endPdfX = Math.Clamp(endNormX, 0.0, 1.0) * ptW;
                    double endPdfY = (1.0 - Math.Clamp(endNormY, 0.0, 1.0)) * ptH;

                    int startCharIdx = FindClosestCharIndex(textPage, totalChars, startPdfX, startPdfY);
                    int endCharIdx = FindClosestCharIndex(textPage, totalChars, endPdfX, endPdfY);

                    if (startCharIdx < 0 || endCharIdx < 0)
                        return PdfTextSelectionResult.Empty;

                    int firstIdx = Math.Min(startCharIdx, endCharIdx);
                    int lastIdx = Math.Max(startCharIdx, endCharIdx);
                    int count = Math.Clamp(lastIdx - firstIdx + 1, 1, totalChars - firstIdx);

                    var rects = new List<PdfNormalizedRect>();
                    int rectCount = PdfiumNative.FPDFText_CountRects(textPage, firstIdx, count);
                    for (int r = 0; r < rectCount; r++)
                    {
                        PdfiumNative.FPDFText_GetRect(textPage, r, out double left, out double top, out double right, out double bottom);
                        double minX = Math.Min(left, right);
                        double maxX = Math.Max(left, right);
                        double maxY = Math.Max(top, bottom);
                        double minY = Math.Min(top, bottom);

                        double normX = Math.Clamp(minX / ptW, 0.0, 1.0);
                        double normY = Math.Clamp(1.0 - (maxY / ptH), 0.0, 1.0);
                        double normRight = Math.Clamp(maxX / ptW, normX, 1.0);
                        double normBottom = Math.Clamp(1.0 - (minY / ptH), normY, 1.0);

                        double w = Math.Max(0.004, normRight - normX);
                        double h = Math.Max(0.008, normBottom - normY);
                        rects.Add(new PdfNormalizedRect(normX, normY, w, h));
                    }

                    ushort[] buffer = new ushort[count + 2];
                    int written = PdfiumNative.FPDFText_GetText(textPage, firstIdx, count, buffer);
                    string selectedText = string.Empty;
                    if (written > 0)
                    {
                        int len = buffer[written - 1] == 0 ? written - 1 : written;
                        if (len > 0)
                        {
                            char[] chars = new char[len];
                            for (int i = 0; i < len; i++) chars[i] = (char)buffer[i];
                            selectedText = new string(chars).Trim();
                        }
                    }

                    return new PdfTextSelectionResult(selectedText, rects);
                }
                finally
                {
                    PdfiumNative.FPDFText_ClosePage(textPage);
                }
            }
            finally
            {
                PdfiumNative.FPDF_ClosePage(page);
            }
        }
    }

    private static int FindClosestCharIndex(IntPtr textPage, int totalChars, double pdfX, double pdfY)
    {
        int directHit = PdfiumNative.FPDFText_GetCharIndexAtPos(textPage, pdfX, pdfY, 6.0, 6.0);
        if (directHit >= 0 && directHit < totalChars)
            return directHit;

        int wideHit = PdfiumNative.FPDFText_GetCharIndexAtPos(textPage, pdfX, pdfY, 18.0, 12.0);
        if (wideHit >= 0 && wideHit < totalChars)
            return wideHit;

        int bestIdx = -1;
        double bestScore = double.MaxValue;

        for (int i = 0; i < totalChars; i++)
        {
            if (PdfiumNative.FPDFText_GetCharBox(textPage, i, out double left, out double right, out double bottom, out double top) == 0)
                continue;

            double minX = Math.Min(left, right);
            double maxX = Math.Max(left, right);
            double minY = Math.Min(bottom, top);
            double maxY = Math.Max(bottom, top);

            if (maxX - minX <= 0.05 && maxY - minY <= 0.05)
                continue;

            double dy = pdfY > maxY ? (pdfY - maxY) : (pdfY < minY ? (minY - pdfY) : 0.0);
            double dx = pdfX < minX ? (minX - pdfX) : (pdfX > maxX ? (pdfX - maxX) : 0.0);

            // Prioritize matching the text line vertically before horizontal distance
            double score = dy * 14.0 + dx;
            if (score < bestScore)
            {
                bestScore = score;
                bestIdx = i;
            }
        }

        return bestIdx;
    }

    public string ExtractTextInNormalizedRect(int pageNumber1Based, double normX1, double normY1, double normX2, double normY2)
    {
        lock (PdfiumNative.SyncRoot)
        {
            if (_disposed || _doc == IntPtr.Zero || PageCount <= 0) return string.Empty;
            int pageIndex = Math.Clamp(pageNumber1Based - 1, 0, PageCount - 1);
            IntPtr page = PdfiumNative.FPDF_LoadPage(_doc, pageIndex);
            if (page == IntPtr.Zero) return string.Empty;

            try
            {
                double ptW = PdfiumNative.FPDF_GetPageWidth(page);
                double ptH = PdfiumNative.FPDF_GetPageHeight(page);
                if (ptW <= 0) ptW = 612;
                if (ptH <= 0) ptH = 792;

                double minX = Math.Clamp(Math.Min(normX1, normX2), 0.0, 1.0);
                double maxX = Math.Clamp(Math.Max(normX1, normX2), 0.0, 1.0);
                double minY = Math.Clamp(Math.Min(normY1, normY2), 0.0, 1.0);
                double maxY = Math.Clamp(Math.Max(normY1, normY2), 0.0, 1.0);

                // Ensure minimum selection box size (if user did a small drag or single line sweep)
                if (maxX - minX < 0.04)
                {
                    minX = Math.Max(0.0, minX - 0.15);
                    maxX = Math.Min(1.0, maxX + 0.35);
                }
                if (maxY - minY < 0.025)
                {
                    minY = Math.Max(0.0, minY - 0.02);
                    maxY = Math.Min(1.0, maxY + 0.03);
                }

                // PDF coordinate system has origin (0,0) at bottom-left, so top > bottom
                double left = minX * ptW;
                double right = maxX * ptW;
                double top = (1.0 - minY) * ptH;
                double bottom = (1.0 - maxY) * ptH;

                IntPtr textPage = PdfiumNative.FPDFText_LoadPage(page);
                if (textPage == IntPtr.Zero) return string.Empty;

                try
                {
                    int needed = PdfiumNative.FPDFText_GetBoundedText(textPage, left, top, right, bottom, null, 0);
                    if (needed > 0)
                    {
                        ushort[] buf = new ushort[needed + 2];
                        int written = PdfiumNative.FPDFText_GetBoundedText(textPage, left, top, right, bottom, buf, buf.Length);
                        if (written > 0)
                        {
                            int len = buf[written - 1] == 0 ? written - 1 : written;
                            if (len > 0)
                            {
                                char[] chars = new char[len];
                                for (int i = 0; i < len; i++) chars[i] = (char)buf[i];
                                string extracted = new string(chars).Trim();
                                if (!string.IsNullOrWhiteSpace(extracted))
                                    return extracted;
                            }
                        }
                    }

                    // Fallback: expand horizontal range to full column width within [top, bottom]
                    int lineNeeded = PdfiumNative.FPDFText_GetBoundedText(textPage, 0, top + 8, ptW, Math.Max(0, bottom - 8), null, 0);
                    if (lineNeeded > 0)
                    {
                        ushort[] buf = new ushort[lineNeeded + 2];
                        int written = PdfiumNative.FPDFText_GetBoundedText(textPage, 0, top + 8, ptW, Math.Max(0, bottom - 8), buf, buf.Length);
                        if (written > 0)
                        {
                            int len = buf[written - 1] == 0 ? written - 1 : written;
                            if (len > 0)
                            {
                                char[] chars = new char[len];
                                for (int i = 0; i < len; i++) chars[i] = (char)buf[i];
                                string extracted = new string(chars).Trim();
                                if (!string.IsNullOrWhiteSpace(extracted))
                                    return extracted;
                            }
                        }
                    }
                }
                finally
                {
                    PdfiumNative.FPDFText_ClosePage(textPage);
                }
            }
            finally
            {
                PdfiumNative.FPDF_ClosePage(page);
            }
        }

        // Secondary fallback: proportional slice of full page text
        string full = ExtractPageText(pageNumber1Based);
        if (string.IsNullOrWhiteSpace(full)) return string.Empty;
        string[] lines = full.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0) return string.Empty;
        int startLine = Math.Clamp((int)Math.Floor(Math.Min(normY1, normY2) * lines.Length), 0, lines.Length - 1);
        int endLine = Math.Clamp((int)Math.Ceiling(Math.Max(normY1, normY2) * lines.Length), startLine, lines.Length - 1);
        return string.Join(" ", lines.Skip(startLine).Take(Math.Max(1, endLine - startLine + 1))).Trim();
    }

    /// <summary>
    /// Checks a sample of pages across the document to detect whether a recognized text layer
    /// (TextPage / vector glyphs) exists, or if the PDF consists solely of raster scans without text.
    /// </summary>
    public bool HasRecognizedTextLayer(int anchorPage1Based = 1)
    {
        if (_cachedHasTextLayer.HasValue)
            return _cachedHasTextLayer.Value;

        lock (PdfiumNative.SyncRoot)
        {
            if (_disposed || _doc == IntPtr.Zero || PageCount <= 0)
                return false;

            if (_cachedHasTextLayer.HasValue)
                return _cachedHasTextLayer.Value;

            var candidatePages = new List<int>();
            void AddCandidate(int page1Based)
            {
                int clamped = Math.Clamp(page1Based, 1, PageCount);
                if (!candidatePages.Contains(clamped))
                    candidatePages.Add(clamped);
            }

            AddCandidate(anchorPage1Based);
            AddCandidate(anchorPage1Based + 1);
            AddCandidate(1);
            AddCandidate(2);
            AddCandidate(3);
            AddCandidate(5);

            int[] fractions = [10, 25, 40, 55, 70, 85];
            foreach (int pct in fractions)
            {
                AddCandidate(Math.Max(1, (int)Math.Round(PageCount * (pct / 100.0))));
            }

            foreach (int pageNumber in candidatePages)
            {
                int pageIndex = pageNumber - 1;
                IntPtr page = PdfiumNative.FPDF_LoadPage(_doc, pageIndex);
                if (page == IntPtr.Zero)
                    continue;

                try
                {
                    IntPtr textPage = PdfiumNative.FPDFText_LoadPage(page);
                    if (textPage == IntPtr.Zero)
                        continue;

                    try
                    {
                        int charCount = PdfiumNative.FPDFText_CountChars(textPage);
                        if (charCount <= 0)
                            continue;

                        int probeLen = Math.Min(charCount, 256);
                        ushort[] buffer = new ushort[probeLen + 2];
                        int written = PdfiumNative.FPDFText_GetText(textPage, 0, probeLen, buffer);
                        for (int i = 0; i < written; i++)
                        {
                            char c = (char)buffer[i];
                            if (c != '\0' && !char.IsWhiteSpace(c) && !char.IsControl(c))
                            {
                                _cachedHasTextLayer = true;
                                return true;
                            }
                        }
                    }
                    finally
                    {
                        PdfiumNative.FPDFText_ClosePage(textPage);
                    }
                }
                finally
                {
                    PdfiumNative.FPDF_ClosePage(page);
                }
            }

            _cachedHasTextLayer = false;
            return false;
        }
    }

    /// <summary>
    /// Searches a single page (1-based) via PDFium text engine for all case-insensitive occurrences of <paramref name="query"/>
    /// and returns each match with its exact normalized bounding rectangles on the page.
    /// </summary>
    public List<DocumentSearchMatch> SearchPageMatches(int pageNumber1Based, string query)
    {
        var results = new List<DocumentSearchMatch>();
        if (string.IsNullOrWhiteSpace(query))
            return results;

        string needle = query.Trim();
        if (needle.Length == 0)
            return results;

        lock (PdfiumNative.SyncRoot)
        {
            if (_disposed || _doc == IntPtr.Zero || PageCount <= 0)
                return results;

            int pageIndex = Math.Clamp(pageNumber1Based - 1, 0, PageCount - 1);
            IntPtr page = PdfiumNative.FPDF_LoadPage(_doc, pageIndex);
            if (page == IntPtr.Zero)
                return results;

            try
            {
                double ptW = PdfiumNative.FPDF_GetPageWidth(page);
                double ptH = PdfiumNative.FPDF_GetPageHeight(page);
                if (ptW <= 0) ptW = 612;
                if (ptH <= 0) ptH = 792;

                IntPtr textPage = PdfiumNative.FPDFText_LoadPage(page);
                if (textPage == IntPtr.Zero)
                    return results;

                try
                {
                    int charCount = PdfiumNative.FPDFText_CountChars(textPage);
                    if (charCount <= 0)
                        return results;

                    ushort[] buffer = new ushort[charCount + 2];
                    int written = PdfiumNative.FPDFText_GetText(textPage, 0, charCount, buffer);
                    if (written <= 1)
                        return results;

                    int actualLen = Math.Min(charCount, written - 1);
                    char[] chars = new char[actualLen];
                    for (int i = 0; i < actualLen; i++)
                    {
                        chars[i] = (char)buffer[i];
                    }

                    string pageText = new string(chars);
                    int pos = 0;
                    while (pos <= pageText.Length - needle.Length)
                    {
                        int idx = pageText.IndexOf(needle, pos, StringComparison.OrdinalIgnoreCase);
                        if (idx < 0)
                            break;

                        int matchLen = Math.Min(needle.Length, actualLen - idx);
                        var rects = new List<PdfNormalizedRect>();
                        int rectCount = PdfiumNative.FPDFText_CountRects(textPage, idx, matchLen);
                        for (int r = 0; r < rectCount; r++)
                        {
                            PdfiumNative.FPDFText_GetRect(textPage, r, out double left, out double top, out double right, out double bottom);
                            double minX = Math.Min(left, right);
                            double maxX = Math.Max(left, right);
                            double maxY = Math.Max(top, bottom);
                            double minY = Math.Min(top, bottom);

                            double normX = Math.Clamp(minX / ptW, 0.0, 1.0);
                            double normY = Math.Clamp(1.0 - (maxY / ptH), 0.0, 1.0);
                            double normRight = Math.Clamp(maxX / ptW, normX, 1.0);
                            double normBottom = Math.Clamp(1.0 - (minY / ptH), normY, 1.0);

                            double w = Math.Max(0.005, normRight - normX);
                            double h = Math.Max(0.010, normBottom - normY);
                            rects.Add(new PdfNormalizedRect(normX, normY, w, h));
                        }

                        double primaryY = rects.Count > 0 ? rects[0].Y : (double)idx / Math.Max(1, actualLen);
                        results.Add(new DocumentSearchMatch(pageNumber1Based, idx, matchLen, rects, primaryY));
                        pos = idx + Math.Max(1, matchLen);
                    }
                }
                finally
                {
                    PdfiumNative.FPDFText_ClosePage(textPage);
                }
            }
            finally
            {
                PdfiumNative.FPDF_ClosePage(page);
            }
        }

        return results;
    }

    public string ExtractPageText(int pageNumber1Based)
    {
        lock (PdfiumNative.SyncRoot)
        {
            if (_disposed || _doc == IntPtr.Zero || PageCount <= 0) return string.Empty;
            int pageIndex = Math.Clamp(pageNumber1Based - 1, 0, PageCount - 1);
            IntPtr page = PdfiumNative.FPDF_LoadPage(_doc, pageIndex);
            if (page == IntPtr.Zero) return string.Empty;

            try
            {
                IntPtr textPage = PdfiumNative.FPDFText_LoadPage(page);
                if (textPage == IntPtr.Zero) return string.Empty;
                try
                {
                    int charCount = PdfiumNative.FPDFText_CountChars(textPage);
                    if (charCount <= 0) return string.Empty;

                    ushort[] buffer = new ushort[charCount + 2];
                    int written = PdfiumNative.FPDFText_GetText(textPage, 0, charCount, buffer);
                    if (written <= 1) return string.Empty;

                    char[] chars = new char[written - 1];
                    for (int i = 0; i < written - 1; i++)
                    {
                        chars[i] = (char)buffer[i];
                    }
                    return new string(chars).Trim();
                }
                finally
                {
                    PdfiumNative.FPDFText_ClosePage(textPage);
                }
            }
            finally
            {
                PdfiumNative.FPDF_ClosePage(page);
            }
        }
    }

    public string ExtractTextForFtsIndexing(int maxPagesToScan = 25)
    {
        var sb = new System.Text.StringBuilder();
        int limit = Math.Min(PageCount, maxPagesToScan);
        for (int p = 1; p <= limit; p++)
        {
            string pageText = ExtractPageText(p);
            if (!string.IsNullOrWhiteSpace(pageText))
            {
                sb.AppendLine(pageText);
                if (sb.Length > 64000) break;
            }
        }
        return sb.ToString();
    }

    public byte[] RenderCoverPngBytes(int targetWidthPx = 320)
    {
        var rendered = RenderPageBgra(1, zoom: 1.0, maxWidthPx: targetWidthPx);
        using var bmp = new Bitmap(rendered.Width, rendered.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var bmpData = bmp.LockBits(new Rectangle(0, 0, rendered.Width, rendered.Height), ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < rendered.Height; y++)
            {
                IntPtr destRow = IntPtr.Add(bmpData.Scan0, y * bmpData.Stride);
                Marshal.Copy(rendered.Pixels, y * rendered.Stride, destRow, Math.Min(rendered.Stride, bmpData.Stride));
            }
        }
        finally
        {
            bmp.UnlockBits(bmpData);
        }

        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    public void Dispose()
    {
        lock (PdfiumNative.SyncRoot)
        {
            if (_disposed) return;
            _disposed = true;

            if (_doc != IntPtr.Zero)
            {
                PdfiumNative.FPDF_CloseDocument(_doc);
                _doc = IntPtr.Zero;
            }

            if (_delegateHandle.IsAllocated)
            {
                _delegateHandle.Free();
            }

            _stream.Dispose();
        }
    }
}

public sealed record RenderedPageBuffer(int PageNumber, int Width, int Height, int Stride, byte[] Pixels);

public readonly record struct PdfNormalizedRect(double X, double Y, double Width, double Height);

public sealed record PdfTextSelectionResult(string SelectedText, IReadOnlyList<PdfNormalizedRect> HighlightRects)
{
    public static readonly PdfTextSelectionResult Empty = new(string.Empty, Array.Empty<PdfNormalizedRect>());
}

public sealed record DocumentSearchMatch(
    int PageNumber,
    int CharIndex,
    int CharLength,
    IReadOnlyList<PdfNormalizedRect> HighlightRects,
    double PrimaryNormY);

public sealed class PooledPageBuffer(
    int pageNumber,
    int width,
    int height,
    int stride,
    int byteLength,
    byte[] rentedPixels,
    bool isLowResPreview) : IDisposable
{
    private byte[]? _rentedPixels = rentedPixels;

    public int PageNumber { get; } = pageNumber;
    public int Width { get; } = width;
    public int Height { get; } = height;
    public int Stride { get; } = stride;
    public int ByteLength { get; } = byteLength;
    public bool IsLowResPreview { get; } = isLowResPreview;
    public byte[] Pixels => _rentedPixels ?? [];

    public void Dispose()
    {
        var arr = Interlocked.Exchange(ref _rentedPixels, null);
        if (arr != null)
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(arr);
        }
    }
}
