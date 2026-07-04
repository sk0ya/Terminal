using Terminal.Sessions;
using Terminal.Tabs;

namespace Terminal.Tests;

public sealed class TerminalSessionLifecycleCoordinatorTests
{
    [Fact]
    public async Task TransitionGateSerializesStartAndStopOwnership()
    {
        var coordinator = new TerminalSessionLifecycleCoordinator();
        await coordinator.BeginTransitionAsync();

        Task waiter = coordinator.BeginTransitionAsync();
        Assert.True(coordinator.IsTransitionActive);
        Assert.False(waiter.IsCompleted);

        coordinator.EndTransition();
        await waiter.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(coordinator.IsTransitionActive);
        coordinator.EndTransition();
        Assert.False(coordinator.IsTransitionActive);
    }

    [Fact]
    public void AttachAdvancesGenerationAndMakesPreviousExitStale()
    {
        var coordinator = new TerminalSessionLifecycleCoordinator();
        var first = new FakeSession();
        var second = new FakeSession();
        coordinator.Attach(first);
        Assert.True(coordinator.TryClaimExit(first, out long firstGeneration));

        long secondGeneration = coordinator.Attach(second);

        Assert.True(secondGeneration > firstGeneration);
        Assert.False(coordinator.ShouldContinueExit(first, firstGeneration));
        Assert.False(coordinator.TryClaimExit(first, out _));
        Assert.True(coordinator.IsCurrent(second));
    }

    [Fact]
    public void ExitCanBeClaimedOnlyOnceAndOnlyByCurrentSession()
    {
        var coordinator = new TerminalSessionLifecycleCoordinator();
        var current = new FakeSession();
        var stale = new FakeSession();
        coordinator.Attach(current);

        Assert.False(coordinator.TryClaimExit(stale, out _));
        Assert.True(coordinator.TryClaimExit(current, out long generation));
        Assert.True(coordinator.ShouldContinueExit(current, generation));
        Assert.False(coordinator.TryClaimExit(current, out _));
    }

    [Fact]
    public void ExpectedSessionAndDetachProtectReplacementSession()
    {
        var coordinator = new TerminalSessionLifecycleCoordinator();
        var current = new FakeSession();
        var stale = new FakeSession();
        coordinator.Attach(current);

        Assert.True(coordinator.MatchesExpected(null));
        Assert.True(coordinator.MatchesExpected(current));
        Assert.False(coordinator.MatchesExpected(stale));
        Assert.Same(current, coordinator.DetachCurrent());
        Assert.Null(coordinator.Current);
        Assert.Null(coordinator.DetachCurrent());
    }

    [Fact]
    public void DisposalOwnershipIsIdempotentPerSession()
    {
        var coordinator = new TerminalSessionLifecycleCoordinator();
        var first = new FakeSession();
        var second = new FakeSession();

        Assert.True(coordinator.TryClaimDisposal(first));
        Assert.False(coordinator.TryClaimDisposal(first));
        Assert.True(coordinator.TryClaimDisposal(second));
    }

    private sealed class FakeSession : ITerminalSession
    {
        public TerminalSessionCapabilities Capabilities { get; } = new(
            TerminalSessionKind.ConPty,
            SupportsResize: true,
            SupportsTerminalInput: true);

        public event EventHandler<string>? OutputReceived;
        public event EventHandler<int>? Exited;

        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public bool IsOutputStalled(TimeSpan initialOutputTimeout, TimeSpan idleOutputTimeout) => false;
        public void Resize(short columns, short rows) { }
        public void Start() { }
        public bool TryForceUnlock(uint exitCode = 1) => true;
        public void Write(string input) { }
        public void Write(byte[] input) { }

        public void RaiseOutput(string text) => OutputReceived?.Invoke(this, text);
        public void RaiseExit(int code) => Exited?.Invoke(this, code);
    }
}
