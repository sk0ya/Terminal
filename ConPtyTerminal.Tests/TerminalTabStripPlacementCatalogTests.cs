namespace ConPtyTerminal.Tests;

public sealed class TerminalTabStripPlacementCatalogTests
{
    [Theory]
    [InlineData(null, TerminalTabStripPlacementCatalog.Top)]
    [InlineData("", TerminalTabStripPlacementCatalog.Top)]
    [InlineData("TOP", TerminalTabStripPlacementCatalog.Top)]
    [InlineData("bottom", TerminalTabStripPlacementCatalog.Bottom)]
    [InlineData("Left", TerminalTabStripPlacementCatalog.Left)]
    [InlineData("right", TerminalTabStripPlacementCatalog.Right)]
    [InlineData("diagonal", TerminalTabStripPlacementCatalog.Top)]
    public void NormalizeReturnsKnownPlacementOrFallsBackToTop(string? rawPlacement, string expectedPlacement)
    {
        string placement = TerminalTabStripPlacementCatalog.Normalize(rawPlacement);

        Assert.Equal(expectedPlacement, placement);
    }

    [Theory]
    [InlineData(TerminalTabStripPlacementCatalog.Top, true)]
    [InlineData(TerminalTabStripPlacementCatalog.Bottom, true)]
    [InlineData(TerminalTabStripPlacementCatalog.Left, false)]
    [InlineData(TerminalTabStripPlacementCatalog.Right, false)]
    public void IsHorizontalMatchesExpectedPlacements(string placement, bool expectedIsHorizontal)
    {
        bool isHorizontal = TerminalTabStripPlacementCatalog.IsHorizontal(placement);

        Assert.Equal(expectedIsHorizontal, isHorizontal);
        Assert.Equal(!expectedIsHorizontal, TerminalTabStripPlacementCatalog.IsVertical(placement));
    }
}
