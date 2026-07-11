using System.Windows.Media;

namespace Terminal.Settings;

public static class TerminalColorThemeCatalog
{
    public static IReadOnlyList<string> SchemeNames { get; } = ["Dark", "Light", "Custom"];

    public static TerminalColorTheme Resolve(TerminalAppSettings settings) => settings.ColorScheme switch
    {
        "Light" => Light,
        "Custom" when TryCreateCustom(settings, out TerminalColorTheme? theme) => theme!,
        _ => TerminalColorTheme.Default
    };

    public static bool TryCreateCustom(TerminalAppSettings settings, out TerminalColorTheme? theme)
    {
        theme = null;
        if (!TryParse(settings.CustomForeground, out Color foreground) ||
            !TryParse(settings.CustomBackground, out Color background) ||
            !TryParse(settings.CustomCursorColor, out Color cursor) ||
            !TryParse(settings.CustomSelectionColor, out Color selection) ||
            settings.CustomAnsiPalette is not { Length: TerminalColorTheme.AnsiPaletteColorCount }) return false;
        var palette = new Color[TerminalColorTheme.AnsiPaletteColorCount];
        for (int i = 0; i < palette.Length; i++) if (!TryParse(settings.CustomAnsiPalette[i], out palette[i])) return false;
        theme = new(foreground, background, palette, cursor, selection);
        return true;
    }

    public static string Format(Color color) => color.A == byte.MaxValue
        ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
        : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    public static bool TryParse(string? text, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        try { color = (Color)ColorConverter.ConvertFromString(text.Trim()); return true; }
        catch (FormatException) { return false; }
    }

    public static TerminalColorTheme Light { get; } = new(
        Color.FromRgb(0x20, 0x20, 0x20), Color.FromRgb(0xFA, 0xFA, 0xFA),
        [Color.FromRgb(0,0,0), Color.FromRgb(0xC5,0x0F,0x1F), Color.FromRgb(0x13,0x7A,0x0E), Color.FromRgb(0x94,0x70,0),
         Color.FromRgb(0,0x37,0xDA), Color.FromRgb(0x88,0x17,0x98), Color.FromRgb(0x1A,0x78,0xB8), Color.FromRgb(0x66,0x66,0x66),
         Color.FromRgb(0x76,0x76,0x76), Color.FromRgb(0xD1,0x34,0x38), Color.FromRgb(0x16,0x98,0x0C), Color.FromRgb(0xA0,0x78,0),
         Color.FromRgb(0x3B,0x68,0xDF), Color.FromRgb(0xA4,0,0x8E), Color.FromRgb(0x20,0xA6,0xA6), Color.FromRgb(0x22,0x22,0x22)],
        Color.FromRgb(0, 0x66, 0xCC), Color.FromArgb(0x55, 0, 0x78, 0xD4));
}
