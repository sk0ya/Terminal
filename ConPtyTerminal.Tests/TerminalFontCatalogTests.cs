namespace ConPtyTerminal.Tests;

public sealed class TerminalFontCatalogTests
{
    [Fact]
    public void BuildAvailableFontFamilyNamesKeepsPreferredFontsFirst()
    {
        IReadOnlyList<string> fontFamilies = TerminalFontCatalog.BuildAvailableFontFamilyNames(
        [
            "Consolas",
            "Arial",
            "Cascadia Mono",
            "Fira Code"
        ]);

        Assert.Equal(
            ["Cascadia Mono", "Consolas", "Fira Code", "Arial"],
            fontFamilies);
    }

    [Fact]
    public void NormalizeFontFamilyNameMatchesAvailableFontsCaseInsensitively()
    {
        string normalized = TerminalFontCatalog.NormalizeFontFamilyName(
            "consolas",
            ["Cascadia Mono", "Consolas", "Arial"]);

        Assert.Equal("Consolas", normalized);
    }

    [Fact]
    public void NormalizeFontFamilyNameFallsBackToFirstAvailableChoice()
    {
        string normalized = TerminalFontCatalog.NormalizeFontFamilyName(
            "Missing Font",
            ["Cascadia Mono", "Consolas"]);

        Assert.Equal("Cascadia Mono", normalized);
    }
}
