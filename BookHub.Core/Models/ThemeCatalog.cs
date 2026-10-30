namespace BookHub.Core.Models;

public sealed record ThemePreset(
    string Name,
    string BgPrimary,
    string BgSecondary,
    string BgCard,
    string BgCardHover,
    string BorderSubtle,
    string Accent,
    string AccentHover,
    string TextPrimary,
    string TextSecondary,
    string TextMuted,
    string ReaderViewportBg,
    string ReaderPageBg,
    string ReaderPageText,
    uint PdfCanvasArgb,
    string? InputBackground = null,
    string? Border = null,
    bool IsLight = false)
{
    public uint PageBgArgb => ParseHexToArgb(ReaderPageBg, 0xFFFFFFFF);
    public uint PageTextArgb => ParseHexToArgb(ReaderPageText, 0xFF000000);

    private static uint ParseHexToArgb(string? hex, uint fallback)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return fallback;

        ReadOnlySpan<char> span = hex.AsSpan().Trim();
        if (span.StartsWith("#"))
            span = span[1..];

        if (span.Length == 6 && uint.TryParse(span, System.Globalization.NumberStyles.HexNumber, null, out uint rgb))
        {
            return 0xFF000000u | rgb;
        }
        if (span.Length == 8 && uint.TryParse(span, System.Globalization.NumberStyles.HexNumber, null, out uint argb))
        {
            return argb;
        }
        return fallback;
    }
}

public static class ThemeCatalog
{
    public static readonly IReadOnlyList<ThemePreset> Presets =
    [
        // Dark Themes
        new("Obsidian Dark",
            BgPrimary: "#161618", BgSecondary: "#1E1E22", BgCard: "#26262B", BgCardHover: "#303038",
            BorderSubtle: "#383842", Accent: "#8B5CF6", AccentHover: "#7C3AED",
            TextPrimary: "#DCDCDD", TextSecondary: "#9E9EA8", TextMuted: "#6E6E77",
            ReaderViewportBg: "#121214", ReaderPageBg: "#1E1E22", ReaderPageText: "#E4E4E7",
            PdfCanvasArgb: 0xFFF4F4F6,
            InputBackground: "#1F1F24", Border: "#383842", IsLight: false),

        new("Nord Dark",
            BgPrimary: "#2E3440", BgSecondary: "#3B4252", BgCard: "#434C5E", BgCardHover: "#4C566A",
            BorderSubtle: "#59647A", Accent: "#88C0D0", AccentHover: "#5E81AC",
            TextPrimary: "#ECEFF4", TextSecondary: "#D8DEE9", TextMuted: "#9BA6B8",
            ReaderViewportBg: "#242933", ReaderPageBg: "#2E3440", ReaderPageText: "#ECEFF4",
            PdfCanvasArgb: 0xFFECEFF4,
            InputBackground: "#3B4252", Border: "#59647A", IsLight: false),

        new("OLED Black",
            BgPrimary: "#000000", BgSecondary: "#08080C", BgCard: "#10121A", BgCardHover: "#191C28",
            BorderSubtle: "#222634", Accent: "#3B82F6", AccentHover: "#2563EB",
            TextPrimary: "#F9FAFB", TextSecondary: "#9CA3AF", TextMuted: "#6B7280",
            ReaderViewportBg: "#000000", ReaderPageBg: "#0A0B0F", ReaderPageText: "#E5E7EB",
            PdfCanvasArgb: 0xFFECEFF4,
            InputBackground: "#08080C", Border: "#222634", IsLight: false),

        // Light Themes
        new("Nord Light",
            BgPrimary: "#ECEFF4", BgSecondary: "#E5E9F0", BgCard: "#FFFFFF", BgCardHover: "#D8DEE9",
            BorderSubtle: "#C8D0DF", Accent: "#5E81AC", AccentHover: "#4C6A92",
            TextPrimary: "#2E3440", TextSecondary: "#4C566A", TextMuted: "#7B88A1",
            ReaderViewportBg: "#D8DEE9", ReaderPageBg: "#FAFAFC", ReaderPageText: "#2E3440",
            PdfCanvasArgb: 0xFFFAFAFC,
            InputBackground: "#FFFFFF", Border: "#D8DEE9", IsLight: true),

        new("Clean White",
            BgPrimary: "#F8FAFC", BgSecondary: "#F1F5F9", BgCard: "#FFFFFF", BgCardHover: "#E2E8F0",
            BorderSubtle: "#CBD5E1", Accent: "#2563EB", AccentHover: "#1D4ED8",
            TextPrimary: "#0F172A", TextSecondary: "#475569", TextMuted: "#64748B",
            ReaderViewportBg: "#E2E8F0", ReaderPageBg: "#FFFFFF", ReaderPageText: "#0F172A",
            PdfCanvasArgb: 0xFFFFFFFF,
            InputBackground: "#FFFFFF", Border: "#CBD5E1", IsLight: true),

        new("Warm Sepia",
            BgPrimary: "#F4ECD8", BgSecondary: "#EAE0C8", BgCard: "#FBF6E9", BgCardHover: "#E5D9BC",
            BorderSubtle: "#D1C3A3", Accent: "#B45309", AccentHover: "#92400E",
            TextPrimary: "#3B2F2F", TextSecondary: "#615049", TextMuted: "#87746B",
            ReaderViewportBg: "#E6D9BE", ReaderPageBg: "#FBF5E6", ReaderPageText: "#2E2421",
            PdfCanvasArgb: 0xFFFBF5E6,
            InputBackground: "#FAF4E6", Border: "#D1C3A3", IsLight: true),

        // Additional Suite Presets for backward compatibility
        new("Midnight Navy",
            BgPrimary: "#0A1124", BgSecondary: "#0F1932", BgCard: "#162447", BgCardHover: "#1E315E",
            BorderSubtle: "#283D70", Accent: "#38BDF8", AccentHover: "#0284C7",
            TextPrimary: "#F0F9FF", TextSecondary: "#94A3B8", TextMuted: "#64748B",
            ReaderViewportBg: "#070C1A", ReaderPageBg: "#111C38", ReaderPageText: "#E2E8F0",
            PdfCanvasArgb: 0xFFF1F5F9,
            InputBackground: "#0F1932", Border: "#283D70", IsLight: false),

        new("Nord Frost",
            BgPrimary: "#2E3440", BgSecondary: "#3B4252", BgCard: "#434C5E", BgCardHover: "#4C566A",
            BorderSubtle: "#59647A", Accent: "#88C0D0", AccentHover: "#5E81AC",
            TextPrimary: "#ECEFF4", TextSecondary: "#D8DEE9", TextMuted: "#9BA6B8",
            ReaderViewportBg: "#242933", ReaderPageBg: "#2E3440", ReaderPageText: "#ECEFF4",
            PdfCanvasArgb: 0xFFECEFF4,
            InputBackground: "#3B4252", Border: "#59647A", IsLight: false),

        new("Dracula / Monokai",
            BgPrimary: "#1E1F29", BgSecondary: "#282A36", BgCard: "#343746", BgCardHover: "#44475A",
            BorderSubtle: "#52556C", Accent: "#BD93F9", AccentHover: "#FF79C6",
            TextPrimary: "#F8F8F2", TextSecondary: "#BFBFB8", TextMuted: "#6272A4",
            ReaderViewportBg: "#191A22", ReaderPageBg: "#282A36", ReaderPageText: "#F8F8F2",
            PdfCanvasArgb: 0xFFF8F8F2,
            InputBackground: "#282A36", Border: "#52556C", IsLight: false),

        new("Warm Sepia Dark",
            BgPrimary: "#1C1713", BgSecondary: "#251F19", BgCard: "#302820", BgCardHover: "#3C3228",
            BorderSubtle: "#4A3E32", Accent: "#D97706", AccentHover: "#B45309",
            TextPrimary: "#F3E5D0", TextSecondary: "#C4B299", TextMuted: "#8F7E68",
            ReaderViewportBg: "#16120E", ReaderPageBg: "#241E18", ReaderPageText: "#EAD8C0",
            PdfCanvasArgb: 0xFFF5E9D4,
            InputBackground: "#251F19", Border: "#4A3E32", IsLight: false),

        new("Paper Sepia Classic",
            BgPrimary: "#F4ECD8", BgSecondary: "#EAE0C8", BgCard: "#FBF6E9", BgCardHover: "#E5D9BC",
            BorderSubtle: "#D1C3A3", Accent: "#B45309", AccentHover: "#92400E",
            TextPrimary: "#3B2F2F", TextSecondary: "#615049", TextMuted: "#87746B",
            ReaderViewportBg: "#E6D9BE", ReaderPageBg: "#FBF5E6", ReaderPageText: "#2E2421",
            PdfCanvasArgb: 0xFFFBF5E6,
            InputBackground: "#FAF4E6", Border: "#D1C3A3", IsLight: true),

        new("Solarized Light",
            BgPrimary: "#FDF6E3", BgSecondary: "#EEE8D5", BgCard: "#FAF2DC", BgCardHover: "#E4DEC9",
            BorderSubtle: "#D3CBB7", Accent: "#268BD2", AccentHover: "#1E6FA8",
            TextPrimary: "#073642", TextSecondary: "#586E75", TextMuted: "#839496",
            ReaderViewportBg: "#E8E2CF", ReaderPageBg: "#FDF6E3", ReaderPageText: "#073642",
            PdfCanvasArgb: 0xFFFDF6E3,
            InputBackground: "#FAF2DC", Border: "#D3CBB7", IsLight: true)
    ];

    public static ThemePreset GetByName(string? themeName) =>
        Presets.FirstOrDefault(p => p.Name.Equals(themeName, StringComparison.OrdinalIgnoreCase))
        ?? Presets[0];
}
