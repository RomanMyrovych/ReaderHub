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
            return ReadBookmarksRecursive(IntPtr.Zero, visited, 0);
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

            var item = new TocItem
            {
                Title = string.IsNullOrWhiteSpace(title) ? "Розділ" : title,
                PageNumber = pageIndex >= 0 ? pageIndex + 1 : 1,
                Children = ReadBookmarksRecursive(current, visited, depth + 1)
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

    public RenderedPageBuffer RenderPageBgra(int pageNumber1Based, double zoom = 1.25, int maxWidthPx = 0, uint backgroundArgb = 0xFFFFFFFF)
    {
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

                int width = Math.Clamp((int)Math.Round(ptW * scale), 64, 4096);
                int height = Math.Clamp((int)Math.Round(ptH * scale), 64, 4096);

                // FPDFBitmap_BGRA = 4
                IntPtr bmp = PdfiumNative.FPDFBitmap_CreateEx(width, height, 4, IntPtr.Zero, width * 4);
                if (bmp == IntPtr.Zero)
                    throw new OutOfMemoryException("Не вдалося виділити буфер PDFium для рендерингу сторінки.");

                try
                {
                    PdfiumNative.FPDFBitmap_FillRect(bmp, 0, 0, width, height, backgroundArgb);
                    int flags = PdfiumNative.FPDF_ANNOT | PdfiumNative.FPDF_LCD_TEXT | PdfiumNative.FPDF_RENDER_LIMITEDIMAGECACHE;
                    PdfiumNative.FPDF_RenderPageBitmap(bmp, page, 0, 0, width, height, 0, flags);

                    IntPtr bufferPtr = PdfiumNative.FPDFBitmap_GetBuffer(bmp);
                    int stride = PdfiumNative.FPDFBitmap_GetStride(bmp);
                    byte[] pixels = new byte[stride * height];
                    Marshal.Copy(bufferPtr, pixels, 0, pixels.Length);

                    return new RenderedPageBuffer(pageNumber1Based, width, height, stride, pixels);
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
