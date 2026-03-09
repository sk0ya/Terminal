using Terminal.Tabs;

namespace Terminal.Tests;

public sealed class TerminalTabViewViewportTests
{
    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(0.0005, 0, false)]
    [InlineData(0, -0.0005, false)]
    [InlineData(1, 0, true)]
    [InlineData(0, -1, true)]
    [InlineData(0.25, 0.25, true)]
    public void ShouldRefreshViewportSizeRespondsOnlyToMeaningfulViewportChanges(
        double viewportWidthChange,
        double viewportHeightChange,
        bool expected)
    {
        bool result = TerminalTabView.ShouldRefreshViewportSize(viewportWidthChange, viewportHeightChange);

        Assert.Equal(expected, result);
    }
}
