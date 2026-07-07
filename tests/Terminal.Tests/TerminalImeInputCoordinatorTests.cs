using Terminal.Tabs;

namespace Terminal.Tests;

public sealed class TerminalImeInputCoordinatorTests
{
    [Fact]
    public void CompositionStartAndTextUpdateEnableProxyCaret()
    {
        var coordinator = new TerminalImeInputCoordinator();

        coordinator.BeginOrUpdateComposition();
        Assert.True(coordinator.OnProxyTextChanged(hasPendingText: true));

        Assert.True(coordinator.ImeCompositionActive);
        Assert.True(coordinator.HasPendingProxyText);
        Assert.True(coordinator.ShouldUseProxyCaret);
    }

    [Fact]
    public void CommitWithPendingTextSchedulesFlushAndEndsComposition()
    {
        var coordinator = new TerminalImeInputCoordinator();
        coordinator.BeginOrUpdateComposition();

        ImeCommitAction action = coordinator.Commit(hasPendingText: true);

        Assert.Equal(ImeCommitAction.ScheduleCommittedFlush, action);
        Assert.False(coordinator.ImeCompositionActive);
        Assert.True(coordinator.CanFlushProxyText());
        Assert.False(coordinator.ShouldUseProxyCaret);
    }

    [Fact]
    public void CommitWithoutTextRequestsOverlayOnly()
    {
        var coordinator = new TerminalImeInputCoordinator();
        coordinator.BeginOrUpdateComposition();

        ImeCommitAction action = coordinator.Commit(hasPendingText: false);

        Assert.Equal(ImeCommitAction.UpdateOverlay, action);
        Assert.False(coordinator.HasPendingProxyText);
        Assert.False(coordinator.CanFlushProxyText());
    }

    [Fact]
    public void DuplicateTextChangedKeepsStateAndStillRequestsOverlay()
    {
        var coordinator = new TerminalImeInputCoordinator();

        Assert.True(coordinator.OnProxyTextChanged(hasPendingText: true));
        Assert.True(coordinator.OnProxyTextChanged(hasPendingText: true));

        Assert.True(coordinator.HasPendingProxyText);
    }

    [Fact]
    public void DeferredFlushCanBeQueuedAndConsumedOnlyOnce()
    {
        var coordinator = new TerminalImeInputCoordinator();

        Assert.True(coordinator.TryQueueDeferredFlush());
        Assert.False(coordinator.TryQueueDeferredFlush());
        Assert.True(coordinator.TryConsumeDeferredFlush());
        Assert.False(coordinator.TryConsumeDeferredFlush());
    }

    [Fact]
    public void ResetSuppressesTextSelectionAndCommitUntilCompleted()
    {
        var coordinator = new TerminalImeInputCoordinator();
        coordinator.BeginOrUpdateComposition();
        coordinator.OnProxyTextChanged(hasPendingText: true);
        coordinator.TryQueueDeferredFlush();

        coordinator.BeginReset();

        Assert.False(coordinator.OnProxyTextChanged(hasPendingText: false));
        Assert.False(coordinator.ShouldProcessSelectionChange());
        Assert.Equal(ImeCommitAction.None, coordinator.Commit(hasPendingText: false));
        Assert.False(coordinator.ImeCompositionActive);
        Assert.False(coordinator.DeferredFlushQueued);
        Assert.False(coordinator.CanFlushProxyText());

        coordinator.EndReset();
        Assert.True(coordinator.ShouldProcessSelectionChange());
    }
}
