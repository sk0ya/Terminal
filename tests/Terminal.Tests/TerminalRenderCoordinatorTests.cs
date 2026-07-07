using Terminal.Tabs;

namespace Terminal.Tests;

public sealed class TerminalRenderCoordinatorTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(16);
    private static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void FirstRequestDispatchesAndDuplicateRequestIsSuppressed()
    {
        var coordinator = new TerminalRenderCoordinator();

        TerminalRenderDecision first = coordinator.RequestRender(false, true, false, Start, Interval);
        TerminalRenderDecision duplicate = coordinator.RequestRender(false, true, false, Start, Interval);

        Assert.Equal(TerminalRenderAction.Dispatch, first.Action);
        Assert.Equal(TerminalRenderAction.None, duplicate.Action);
        Assert.True(coordinator.IsRenderScheduled);
    }

    [Fact]
    public void RecentRenderStartsThrottleForRemainingInterval()
    {
        var coordinator = new TerminalRenderCoordinator();
        Assert.True(coordinator.TryBeginRender());
        coordinator.EndRender(Start);

        TerminalRenderDecision decision = coordinator.RequestRender(
            false,
            dispatcherAccess: true,
            throttleTimerActive: false,
            Start + TimeSpan.FromMilliseconds(6),
            Interval);

        Assert.Equal(TerminalRenderAction.StartThrottle, decision.Action);
        Assert.Equal(TimeSpan.FromMilliseconds(10), decision.ThrottleDelay);
    }

    [Fact]
    public void ThrottleTickClaimsOneDispatchedRender()
    {
        var coordinator = new TerminalRenderCoordinator();

        Assert.True(coordinator.OnThrottleTick());
        Assert.False(coordinator.OnThrottleTick());
        coordinator.BeginDispatchedRender();
        Assert.False(coordinator.IsRenderScheduled);
    }

    [Theory]
    [InlineData(true, TerminalRenderAction.RenderNow)]
    [InlineData(false, TerminalRenderAction.Dispatch)]
    public void ImmediateRequestStopsThrottleAndChoosesAccessAppropriateAction(
        bool dispatcherAccess,
        object expected)
    {
        var coordinator = new TerminalRenderCoordinator();

        TerminalRenderDecision decision = coordinator.RequestRender(
            immediate: true,
            dispatcherAccess,
            throttleTimerActive: true,
            Start,
            Interval);

        Assert.Equal((TerminalRenderAction)expected, decision.Action);
        Assert.True(decision.StopThrottle);
        Assert.Equal(!dispatcherAccess, coordinator.IsRenderScheduled);
    }

    [Fact]
    public void RenderingOwnershipRejectsReentryUntilCompletion()
    {
        var coordinator = new TerminalRenderCoordinator();

        Assert.True(coordinator.TryBeginRender());
        Assert.True(coordinator.IsRendering);
        Assert.False(coordinator.TryBeginRender());

        coordinator.EndRender(Start);
        Assert.False(coordinator.IsRendering);
        Assert.True(coordinator.TryBeginRender());
    }

    [Fact]
    public void WatchdogArmDisarmAndTickAreSingleOwnerDecisions()
    {
        var coordinator = new TerminalRenderCoordinator();

        Assert.True(coordinator.ArmWatchdog());
        Assert.False(coordinator.ArmWatchdog());
        Assert.True(coordinator.ConsumeWatchdogTick());
        Assert.False(coordinator.ConsumeWatchdogTick());
        Assert.False(coordinator.DisarmWatchdog());

        Assert.True(coordinator.ArmWatchdog());
        Assert.True(coordinator.DisarmWatchdog());
        Assert.False(coordinator.IsWatchdogArmed);
    }
}
