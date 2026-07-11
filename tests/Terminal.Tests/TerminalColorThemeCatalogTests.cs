using Terminal.Settings;

namespace Terminal.Tests;

public sealed class TerminalColorThemeCatalogTests
{
    [Fact]
    public void LightPresetHasLightBackgroundAndSixteenColors()
    {
        Assert.True(TerminalColorThemeCatalog.Light.Background.R > 200);
        Assert.Equal(16, TerminalColorThemeCatalog.Light.AnsiPalette.Count);
    }

    [Fact]
    public void ValidCustomThemeRoundTripsThroughSettings()
    {
        var settings = new TerminalAppSettings
        {
            ColorScheme = "Custom", CustomForeground = "#010203", CustomBackground = "#040506",
            CustomCursorColor = "#070809", CustomSelectionColor = "#80112233",
            CustomAnsiPalette = Enumerable.Repeat("#123456", 16).ToArray()
        };
        var theme = TerminalColorThemeCatalog.Resolve(settings);
        Assert.Equal("#010203", TerminalColorThemeCatalog.Format(theme.Foreground));
        Assert.Equal("#80112233", TerminalColorThemeCatalog.Format(theme.SelectionBackground));
    }

    [Fact]
    public void InvalidCustomThemeFallsBackToDark()
    {
        var theme = TerminalColorThemeCatalog.Resolve(new TerminalAppSettings { ColorScheme = "Custom" });
        Assert.Same(TerminalColorTheme.Default, theme);
    }
}
