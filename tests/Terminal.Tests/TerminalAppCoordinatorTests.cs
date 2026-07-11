using System.IO;
using Terminal;

namespace Terminal.Tests;

public sealed class TerminalAppCoordinatorTests
{
    [Theory]
    [InlineData("mica", "mica")]
    [InlineData("acrylic", "acrylic")]
    [InlineData("mica-alt", "mica-alt")]
    [InlineData("  MICA  ", "mica")]
    [InlineData("AcrYLic", "acrylic")]
    [InlineData("invalid", "none")]
    [InlineData(null, "none")]
    public void NormalizeBackdropTypeUsesSupportedAllowList(string? input, string expected)
        => Assert.Equal(expected, TerminalSettingsEditor.NormalizeBackdropType(input));

    [Fact]
    public void EmptySessionLogDirectorySelectsDefaultWithoutCreatingDirectory()
    {
        Assert.True(TerminalSettingsEditor.TryNormalizeSessionLogDirectory("  ", out string? directory));
        Assert.Null(directory);
    }

    [Fact]
    public void SessionLogDirectoryNormalizesPathWithoutCreatingIt()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "logs");
        Assert.True(TerminalSettingsEditor.TryNormalizeSessionLogDirectory(path, out string? normalized));
        Assert.Equal(Path.GetFullPath(path), normalized);
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public void SessionLogDirectoryRejectsExistingFileButAllowsMissingDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string file = Path.Combine(root, "not-a-directory");
        File.WriteAllText(file, "x");
        try
        {
            Assert.False(TerminalSettingsEditor.TryNormalizeSessionLogDirectory(file, out _));
            string missing = Path.Combine(root, "future-directory");
            Assert.True(TerminalSettingsEditor.TryNormalizeSessionLogDirectory(missing, out string? normalized));
            Assert.Equal(Path.GetFullPath(missing), normalized);
            Assert.False(Directory.Exists(missing));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
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
