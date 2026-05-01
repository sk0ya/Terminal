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

    [Fact]
    public void ResolveRestoredVerticalOffsetPinsAlternateScreenToTop()
    {
        double offset = TerminalTabView.ResolveRestoredVerticalOffset(
            isAlternateScreenActive: true,
            followTerminalOutput: true,
            preservedDistanceFromBottom: 0,
            extentHeight: 1200,
            viewportHeight: 700);

        Assert.Equal(0, offset);
    }

    [Fact]
    public void ResolveRestoredVerticalOffsetFollowsPrimaryScreenBottom()
    {
        double offset = TerminalTabView.ResolveRestoredVerticalOffset(
            isAlternateScreenActive: false,
            followTerminalOutput: true,
            preservedDistanceFromBottom: 0,
            extentHeight: 1200,
            viewportHeight: 700);

        Assert.Equal(500, offset);
    }

    [Fact]
    public void ResolveRestoredVerticalOffsetKeepsPinnedPrimaryScreenDistance()
    {
        double offset = TerminalTabView.ResolveRestoredVerticalOffset(
            isAlternateScreenActive: false,
            followTerminalOutput: false,
            preservedDistanceFromBottom: 120,
            extentHeight: 1200,
            viewportHeight: 700);

        Assert.Equal(380, offset);
    }
}
