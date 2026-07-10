using Terminal.Tabs;

namespace Terminal.Tests;

public sealed class TerminalOutputBatchCoordinatorTests
{
    [Fact]
    public void EnqueueSchedulesOnlyTheFirstPendingBatchAndDrainPreservesOrder()
    {
        var coordinator = new TerminalOutputBatchCoordinator();

        Assert.True(coordinator.Enqueue("one"));
        Assert.False(coordinator.Enqueue("-two"));

        Assert.Equal("one-two", coordinator.Drain());
        Assert.Null(coordinator.Drain());
    }

    [Fact]
    public void EnqueueAfterDrainOwnsTheNextScheduleWithoutDuplicateReschedule()
    {
        var coordinator = new TerminalOutputBatchCoordinator();
        Assert.True(coordinator.Enqueue("first"));
        Assert.Equal("first", coordinator.Drain());

        Assert.True(coordinator.Enqueue("second"));
        Assert.False(coordinator.EnsureFlushScheduled());
        Assert.Equal("second", coordinator.Drain());
    }

    [Fact]
    public void BoundedDrainPreservesTheRemainderForAFollowingDispatcherPass()
    {
        var coordinator = new TerminalOutputBatchCoordinator();
        Assert.True(coordinator.Enqueue("abcdef"));

        Assert.Equal("abc", coordinator.Drain(3));
        Assert.True(coordinator.EnsureFlushScheduled());
        Assert.Equal("def", coordinator.Drain(3));
        Assert.Null(coordinator.Drain(3));
    }

    [Fact]
    public void DrainRejectsANonPositiveLimitWithoutConsumingOutput()
    {
        var coordinator = new TerminalOutputBatchCoordinator();
        Assert.True(coordinator.Enqueue("pending"));

        Assert.Throws<ArgumentOutOfRangeException>(() => coordinator.Drain(0));
        Assert.Equal("pending", coordinator.Drain());
    }

    [Fact]
    public void ClearDropsPendingOutputAndAllowsNewSchedule()
    {
        var coordinator = new TerminalOutputBatchCoordinator();
        Assert.True(coordinator.Enqueue("discard"));

        coordinator.Clear();

        Assert.Null(coordinator.Drain());
        Assert.True(coordinator.Enqueue("next"));
        Assert.Equal("next", coordinator.Drain());
    }

    [Fact]
    public void RenderPriorityIsConsumedOnceAndCanBeCancelled()
    {
        var coordinator = new TerminalOutputBatchCoordinator();
        coordinator.SetPrioritizeNextRender(true);

        Assert.True(coordinator.ConsumeRenderPriority());
        Assert.False(coordinator.ConsumeRenderPriority());

        coordinator.SetPrioritizeNextRender(true);
        coordinator.SetPrioritizeNextRender(false);
        Assert.False(coordinator.ConsumeRenderPriority());
    }

    [Fact]
    public void ConcurrentEnqueueDoesNotLoseOutput()
    {
        var coordinator = new TerminalOutputBatchCoordinator();
        const int count = 100;

        Parallel.For(0, count, index => coordinator.Enqueue($"{index:D3},"));
        string batch = Assert.IsType<string>(coordinator.Drain());
        string[] tokens = batch.Split(',', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(count, tokens.Length);
        Assert.Equal(
            Enumerable.Range(0, count).Select(index => index.ToString("D3")),
            tokens.OrderBy(token => token, StringComparer.Ordinal));
    }
}
