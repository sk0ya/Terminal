using Terminal;

namespace Terminal.Tests;

public sealed class TerminalAppCoordinatorTests
{
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
}
