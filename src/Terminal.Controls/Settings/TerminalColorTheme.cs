using System.Windows.Media;

namespace Terminal.Settings;

public sealed class TerminalColorTheme
{
    public const int AnsiPaletteColorCount = 16;

    private static readonly Color[] DefaultAnsiPaletteColors =
    [
        Color.FromRgb(0x1C, 0x1C, 0x1C),
        Color.FromRgb(0xC5, 0x0F, 0x1F),
        Color.FromRgb(0x13, 0xA1, 0x0E),
        Color.FromRgb(0xC1, 0x9C, 0x00),
        Color.FromRgb(0x00, 0x37, 0xDA),
        Color.FromRgb(0x88, 0x17, 0x98),
        Color.FromRgb(0x3A, 0x96, 0xDD),
        Color.FromRgb(0xCC, 0xCC, 0xCC),
        Color.FromRgb(0x76, 0x76, 0x76),
        Color.FromRgb(0xE7, 0x48, 0x56),
        Color.FromRgb(0x16, 0xC6, 0x0C),
        Color.FromRgb(0xF9, 0xF1, 0xA5),
        Color.FromRgb(0x3B, 0x78, 0xFF),
        Color.FromRgb(0xB4, 0x00, 0x9E),
        Color.FromRgb(0x61, 0xD6, 0xD6),
        Color.FromRgb(0xF2, 0xF2, 0xF2)
    ];

    public TerminalColorTheme(
        Color foreground,
        Color background,
        IReadOnlyList<Color> ansiPalette,
        Color? cursor = null,
        Color? selectionBackground = null)
    {
        ArgumentNullException.ThrowIfNull(ansiPalette);

        if (ansiPalette.Count != AnsiPaletteColorCount)
        {
            throw new ArgumentException(
                $"ANSI palette must contain exactly {AnsiPaletteColorCount} colors.",
                nameof(ansiPalette));
        }

        Foreground = foreground;
        Background = background;
        Cursor = cursor ?? Color.FromRgb(0x5F, 0xAF, 0xFF);
        SelectionBackground = selectionBackground ?? Color.FromArgb(0x66, 0xE1, 0x9A, 0x4A);
        AnsiPalette = ansiPalette.ToArray();
    }

    public Color Foreground { get; }

    public Color Background { get; }

    public Color Cursor { get; }

    public Color SelectionBackground { get; }

    public IReadOnlyList<Color> AnsiPalette { get; }

    public static TerminalColorTheme Default { get; } = new(
        Color.FromRgb(0xE3, 0xE3, 0xE3),
        Color.FromRgb(0x11, 0x11, 0x11),
        DefaultAnsiPaletteColors,
        Color.FromRgb(0x5F, 0xAF, 0xFF),
        Color.FromArgb(0x66, 0xE1, 0x9A, 0x4A));
}
