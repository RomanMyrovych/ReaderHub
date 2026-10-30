using System.Runtime.InteropServices;
using System.Text;

namespace BookHub.Core.Pdf;

public static class PdfiumNative
{
    private const string DllName = "pdfium.dll";
    private static bool _initialized;
    public static readonly object SyncRoot = new();

    static PdfiumNative()
    {
        EnsureInitialized();
    }

    public static void EnsureInitialized()
    {
        lock (SyncRoot)
        {
            if (_initialized) return;

            NativeLibrary.SetDllImportResolver(typeof(PdfiumNative).Assembly, ResolvePdfiumDll);
            FPDF_InitLibrary();
            _initialized = true;
        }
    }

    private static IntPtr ResolvePdfiumDll(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, DllName, StringComparison.OrdinalIgnoreCase))
            return IntPtr.Zero;

        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "win-arm64",
            Architecture.X86 => "win-x86",
            _ => "win-x64"
        };

        string[] candidatePaths =
        [
            Path.Combine(baseDir, "pdfium.dll"),
            Path.Combine(baseDir, "runtimes", arch, "native", "pdfium.dll")
        ];

        foreach (var candidate in candidatePaths)
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
                return handle;
        }

        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FS_SIZEF
    {
        public float Width;
        public float Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FPDF_FILEACCESS
    {
        public uint m_FileLen;
        public IntPtr m_GetBlock;
        public IntPtr m_Param;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int FpdfGetBlockDelegate(IntPtr param, uint position, IntPtr pBuf, uint size);

    public const int FPDF_ANNOT = 0x01;
    public const int FPDF_LCD_TEXT = 0x02;
    public const int FPDF_RENDER_LIMITEDIMAGECACHE = 0x200;
    public const int FPDF_RENDER_NO_SMOOTHTEXT = 0x0800;
    public const int FPDF_RENDER_NO_SMOOTHIMAGE = 0x1000;
    public const int FPDF_RENDER_NO_SMOOTHPATH = 0x2000;

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDF_InitLibrary();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDF_LoadCustomDocument(ref FPDF_FILEACCESS pFileAccess, IntPtr password);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDF_CloseDocument(IntPtr document);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDF_GetPageCount(IntPtr document);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDF_GetPageSizeByIndexF(IntPtr document, int page_index, out FS_SIZEF size);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDF_LoadPage(IntPtr document, int page_index);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDF_ClosePage(IntPtr page);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern double FPDF_GetPageWidth(IntPtr page);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern double FPDF_GetPageHeight(IntPtr page);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDFBitmap_CreateEx(int width, int height, int format, IntPtr first_scan, int stride);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int start_x, int start_y, int size_x, int size_y, int rotate, int flags);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDFBitmap_GetBuffer(IntPtr bitmap);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFBitmap_GetStride(IntPtr bitmap);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDFBitmap_Destroy(IntPtr bitmap);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint FPDF_GetMetaText(IntPtr document, [MarshalAs(UnmanagedType.LPStr)] string tag, byte[]? buffer, uint buflen);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDFBookmark_GetFirstChild(IntPtr document, IntPtr bookmark);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDFBookmark_GetNextSibling(IntPtr document, IntPtr bookmark);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint FPDFBookmark_GetTitle(IntPtr bookmark, byte[]? buffer, uint buflen);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDFBookmark_GetDest(IntPtr document, IntPtr bookmark);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDFBookmark_GetAction(IntPtr bookmark);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDFAction_GetDest(IntPtr document, IntPtr action);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFDest_GetDestPageIndex(IntPtr document, IntPtr dest);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDFText_LoadPage(IntPtr page);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDFText_ClosePage(IntPtr text_page);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFText_CountChars(IntPtr text_page);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFText_GetText(IntPtr text_page, int start_index, int count, ushort[] result);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFText_GetCharIndexAtPos(
        IntPtr text_page,
        double x,
        double y,
        double xTolerance,
        double yTolerance);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFText_GetCharBox(
        IntPtr text_page,
        int index,
        out double left,
        out double right,
        out double bottom,
        out double top);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFText_CountRects(
        IntPtr text_page,
        int start_index,
        int count);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFText_GetRect(
        IntPtr text_page,
        int rect_index,
        out double left,
        out double top,
        out double right,
        out double bottom);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFText_GetBoundedText(
        IntPtr text_page,
        double left,
        double top,
        double right,
        double bottom,
        ushort[]? buffer,
        int buflen);

    public static string GetMetaString(IntPtr doc, string tag)
    {
        uint len = FPDF_GetMetaText(doc, tag, null, 0);
        if (len <= 2) return string.Empty;
        byte[] buf = new byte[len];
        FPDF_GetMetaText(doc, tag, buf, len);
        return Encoding.Unicode.GetString(buf, 0, (int)len - 2).Trim('\0', ' ', '\t', '\r', '\n');
    }

    public static string GetBookmarkTitle(IntPtr bookmark)
    {
        uint len = FPDFBookmark_GetTitle(bookmark, null, 0);
        if (len <= 2) return string.Empty;
        byte[] buf = new byte[len];
        FPDFBookmark_GetTitle(bookmark, buf, len);
        return Encoding.Unicode.GetString(buf, 0, (int)len - 2).Trim('\0', ' ', '\t', '\r', '\n');
    }
}
