using System.IO;
using Terminal;

namespace Terminal.Tests;

public sealed class TerminalAppCoordinatorTests
{
    [Fact]
    public void TabsCanBeReorderedWithoutLosingItems()
    {
        var tabs = new List<string> { "one", "two", "three" };
        Assert.True(TerminalTabCollectionState.MoveItem(tabs, 0, 2));
        Assert.Equal(["two", "three", "one"], tabs);
        Assert.False(TerminalTabCollectionState.MoveItem(tabs, -1, 1));
    }
    [Theory]
    [InlineData(1, 2, true, 1)]
    [InlineData(2, 2, true, 1)]
    [InlineData(0, 2, false, -1)]
    public void ResolvesSelectionAfterClose(int closed, int remaining, bool selected, int expected)
        => Assert.Equal(expected, TerminalTabCollectionState.GetSelectionAfterClose(closed, remaining, selected));

    [Theory]
    [InlineData(0, 3, -1, 2)]
    [InlineData(2, 3, 1, 0)]
    public void MovesSelectionWithWrap(int current, int count, int delta, int expected)
        => Assert.Equal(expected, TerminalTabCollectionState.MoveSelection(current, count, delta));

    [Theory]
    [InlineData("10", 11)]
    [InlineData("18.6", 19)]
    [InlineData("30", 24)]
    public void NormalizesFontSize(string raw, double expected)
    {
        Assert.True(TerminalSettingsEditor.TryNormalizeFontSize(raw, out double actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("top", true, true, "Bottom", -8, 4, 4)]
    [InlineData("bottom", false, true, "Top", -8, -4, -4)]
    [InlineData("left", false, false, "Right", 4, -8, -6)]
    [InlineData("right", false, false, "Left", -4, -8, -6)]
    [InlineData("invalid", true, true, "Bottom", -8, 4, 4)]
    public void ResolvesWindowLayout(
        string placement,
        bool isTop,
        bool isHorizontal,
        string popupEdge,
        double horizontalOffset,
        double profileOffset,
        double appMenuOffset)
    {
        TerminalWindowLayout layout = TerminalWindowLayout.Resolve(placement);

        Assert.Equal(isTop, layout.IsTop);
        Assert.Equal(isHorizontal, layout.IsHorizontal);
        Assert.Equal(popupEdge, layout.PopupEdge.ToString());
        Assert.Equal(horizontalOffset, layout.HorizontalOffset);
        Assert.Equal(profileOffset, layout.ProfilePickerVerticalOffset);
        Assert.Equal(appMenuOffset, layout.AppMenuVerticalOffset);
    }

    [Fact]
    public void InvalidWorkingDirectoryDoesNotLeakUnusablePath()
    {
        string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        Assert.False(TerminalSettingsEditor.TryNormalizeWorkingDirectory(missing, out string normalized));
        Assert.Empty(normalized);
    }
}
