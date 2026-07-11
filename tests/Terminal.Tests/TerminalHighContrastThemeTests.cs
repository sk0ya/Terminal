using System.Windows.Media;

using Terminal.Settings;

namespace Terminal.Tests;

public sealed class TerminalHighContrastThemeTests
{
    [Fact]
    public void CreateHighContrastUsesSystemColorsForPrimaryTerminalColors()
    {
        Color foreground = Color.FromRgb(0xFF, 0xFF, 0x00);
        Color background = Colors.Black;
        Color accent = Color.FromRgb(0x00, 0x80, 0xFF);

        TerminalColorTheme theme = TerminalColorThemeCatalog.CreateHighContrast(
            TerminalColorThemeCatalog.Light, foreground, background, accent);

        Assert.Equal(foreground, theme.Foreground);
        Assert.Equal(background, theme.Background);
        Assert.Equal(accent, theme.Cursor);
        Assert.Equal(Color.FromArgb(0x99, accent.R, accent.G, accent.B), theme.SelectionBackground);
    }

    [Fact]
    public void CreateHighContrastPreservesConfiguredAnsiPalette()
    {
        TerminalColorTheme source = TerminalColorThemeCatalog.Light;

        TerminalColorTheme theme = TerminalColorThemeCatalog.CreateHighContrast(
            source, Colors.White, Colors.Black, Colors.Yellow);

        Assert.Equal(source.AnsiPalette, theme.AnsiPalette);
        Assert.NotSame(source.AnsiPalette, theme.AnsiPalette);
    }

    [Fact]
    public void ResolveEffectiveRestoresConfiguredThemeWhenHighContrastTurnsOff()
    {
        var settings = new TerminalAppSettings { ColorScheme = "Light" };
        TerminalColorTheme highContrast = TerminalColorThemeCatalog.ResolveEffective(
            settings, true, Colors.Yellow, Colors.Black, Colors.Cyan);
        TerminalColorTheme restored = TerminalColorThemeCatalog.ResolveEffective(
            settings, false, Colors.Yellow, Colors.Black, Colors.Cyan);

        Assert.Equal(Colors.Yellow, highContrast.Foreground);
        Assert.Same(TerminalColorThemeCatalog.Light, restored);
    }
}
