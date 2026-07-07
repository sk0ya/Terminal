using Terminal.Tabs;

namespace Terminal.Tests;

public sealed class TerminalTabViewViewportTests
{
    private readonly TerminalViewportCoordinator _coordinator = new(autoFollowThreshold: 2);

    [Fact]
    public void ViewportSizeStartsWithTerminalDefaults()
    {
        Assert.Equal(120, _coordinator.Columns);
        Assert.Equal(30, _coordinator.Rows);
    }

    [Fact]
    public void UpdatingViewportSizeReportsOnlyRealChangesAndOwnsLatestSize()
    {
        Assert.False(_coordinator.UpdateSize(120, 30));

        Assert.True(_coordinator.UpdateSize(132, 41));
        Assert.Equal(132, _coordinator.Columns);
        Assert.Equal(41, _coordinator.Rows);

        Assert.False(_coordinator.UpdateSize(132, 41));
    }

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
        double offset = _coordinator.ResolveRestoredVerticalOffset(
            isAlternateScreenActive: true,
            preservedDistanceFromBottom: 0,
            extentHeight: 1200,
            viewportHeight: 700);

        Assert.Equal(0, offset);
    }

    [Fact]
    public void ResolveRestoredVerticalOffsetFollowsPrimaryScreenBottom()
    {
        double offset = _coordinator.ResolveRestoredVerticalOffset(
            isAlternateScreenActive: false,
            preservedDistanceFromBottom: 0,
            extentHeight: 1200,
            viewportHeight: 700);

        Assert.Equal(500, offset);
    }

    [Fact]
    public void ResolveRestoredVerticalOffsetKeepsPinnedPrimaryScreenDistance()
    {
        _coordinator.StopFollowing();

        double offset = _coordinator.ResolveRestoredVerticalOffset(
            isAlternateScreenActive: false,
            preservedDistanceFromBottom: 120,
            extentHeight: 1200,
            viewportHeight: 700);

        Assert.Equal(380, offset);
    }

    [Fact]
    public void RestoreNearBottomResumesFollowingOutput()
    {
        _coordinator.StopFollowing();

        double offset = _coordinator.ResolveRestoredVerticalOffset(
            isAlternateScreenActive: false,
            preservedDistanceFromBottom: 2,
            extentHeight: 1200,
            viewportHeight: 700);

        Assert.Equal(500, offset);
        Assert.True(_coordinator.FollowOutput);
    }

    [Fact]
    public void AlternateScreenAlwaysFollowsAndModeTransitionIsIdempotent()
    {
        _coordinator.StopFollowing();

        _coordinator.UpdateFollowState(isAlternateScreenActive: true, distanceFromBottom: 500);

        Assert.True(_coordinator.FollowOutput);
        Assert.True(_coordinator.SetAlternateScreenMode(active: true));
        Assert.False(_coordinator.SetAlternateScreenMode(active: true));
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(2.001, false)]
    public void PrimaryScreenFollowStateUsesInclusiveBottomThreshold(
        double distanceFromBottom,
        bool expectedFollow)
    {
        _coordinator.UpdateFollowState(
            isAlternateScreenActive: false,
            distanceFromBottom);

        Assert.Equal(expectedFollow, _coordinator.FollowOutput);
    }

    [Fact]
    public void AlternateScreenModeCanTransitionBackToPrimary()
    {
        Assert.True(_coordinator.SetAlternateScreenMode(active: true));
        Assert.True(_coordinator.SetAlternateScreenMode(active: false));
        Assert.False(_coordinator.SetAlternateScreenMode(active: false));
    }

    [Fact]
    public void PrimaryRestoreReturnsToBottomAfterAlternateScreenRestore()
    {
        _coordinator.StopFollowing();
        Assert.Equal(0, _coordinator.ResolveRestoredVerticalOffset(
            isAlternateScreenActive: true,
            preservedDistanceFromBottom: 0,
            extentHeight: 700,
            viewportHeight: 700));

        double primaryOffset = _coordinator.ResolveRestoredVerticalOffset(
            isAlternateScreenActive: false,
            preservedDistanceFromBottom: 180,
            extentHeight: 1200,
            viewportHeight: 700);

        Assert.Equal(500, primaryOffset);
        Assert.True(_coordinator.FollowOutput);
    }
}
