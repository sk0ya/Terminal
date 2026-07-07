using Terminal.Sessions;
using Terminal.Tabs;

namespace Terminal.Tests;

public sealed class TerminalSessionOrchestratorTests
{
    [Fact]
    public async Task StartFailureDetachesAndDisposesFailedSession()
    {
        var service = new TerminalSessionOrchestrator();
        var failed = new FakeSession { StartError = new InvalidOperationException("start") };

        TerminalSessionStartResult result = await StartAsync(service, failed);

        Assert.False(result.Started);
        Assert.Same(failed.StartError, result.Error);
        Assert.Equal(1, failed.DisposeCount);
        Assert.Null(service.Current);
    }

    [Fact]
    public async Task WireFailureBestEffortUnwiresAndDisposesUnattachedCandidate()
    {
        var service = new TerminalSessionOrchestrator();
        var session = new FakeSession();
        int unwireCount = 0;

        TerminalSessionStartResult result = await service.StartAsync(
            () => Task.FromResult<ITerminalSession>(session),
            _ => throw new InvalidOperationException("wire"),
            _ => unwireCount++,
            () => { },
            () => false);

        Assert.False(result.Started);
        Assert.IsType<InvalidOperationException>(result.Error);
        Assert.Equal(1, unwireCount);
        Assert.Equal(1, session.DisposeCount);
        Assert.Null(service.Current);
    }

    [Fact]
    public async Task StartResetFailureReturnsErrorAfterPreviousCleanup()
    {
        var service = new TerminalSessionOrchestrator();
        var previous = new FakeSession();
        await StartAsync(service, previous);

        TerminalSessionStartResult result = await service.StartAsync(
            () => Task.FromResult<ITerminalSession>(new FakeSession()),
            Wire, Unwire,
            () => throw new InvalidOperationException("reset"),
            () => false);

        Assert.False(result.Started);
        Assert.IsType<InvalidOperationException>(result.Error);
        Assert.Equal(1, previous.DisposeCount);
        Assert.Null(service.Current);
    }

    [Fact]
    public async Task ManualRestartDisposesPreviousBeforeStartingReplacement()
    {
        var service = new TerminalSessionOrchestrator();
        var first = new FakeSession();
        var second = new FakeSession();
        Assert.True((await StartAsync(service, first)).Started);

        TerminalSessionStartResult result = await StartAsync(service, second);

        Assert.True(result.Started);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.StartCount);
        Assert.Same(second, service.Current);
    }

    [Fact]
    public async Task StopWhileExitIsDrainingMakesExitHandlingStale()
    {
        var service = new TerminalSessionOrchestrator();
        var session = new FakeSession();
        await StartAsync(service, session);
        var drainEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDrain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<TerminalSessionStopResult> exit = service.HandleExitAsync(
            session, 1, TimeSpan.Zero, _ => { }, Unwire, () => { },
            _ => { drainEntered.SetResult(); return releaseDrain.Task; });
        await drainEntered.Task;

        TerminalSessionStopResult stopped = await service.StopAsync(session, false, Unwire, () => { });
        releaseDrain.SetResult();
        TerminalSessionStopResult exited = await exit;

        Assert.True(stopped.Applied);
        Assert.False(exited.Applied);
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public async Task ReplacementDuringExitDrainIsNotStoppedByStaleExit()
    {
        var service = new TerminalSessionOrchestrator();
        var first = new FakeSession();
        var second = new FakeSession();
        await StartAsync(service, first);
        var drainEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDrain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<TerminalSessionStopResult> exit = service.HandleExitAsync(
            first, 1, TimeSpan.Zero, _ => { }, Unwire, () => { },
            _ => { drainEntered.SetResult(); return releaseDrain.Task; });
        await drainEntered.Task;

        await StartAsync(service, second);
        releaseDrain.SetResult();

        Assert.False((await exit).Applied);
        Assert.Same(second, service.Current);
        Assert.Equal(0, second.DisposeCount);
    }

    [Fact]
    public async Task ForceStopUnlocksAndDisposesCurrentSession()
    {
        var service = new TerminalSessionOrchestrator();
        var session = new FakeSession();
        await StartAsync(service, session);

        TerminalSessionStopResult result = await service.StopAsync(null, true, Unwire, () => { });

        Assert.True(result.Applied);
        Assert.Equal(1, session.ForceUnlockCount);
        Assert.Equal(1, session.DisposeCount);
        Assert.Null(service.Current);
    }

    [Fact]
    public async Task NaturalExitReportsDisposeFailureAsStopError()
    {
        var service = new TerminalSessionOrchestrator();
        var session = new FakeSession { DisposeError = new InvalidOperationException("dispose") };
        await StartAsync(service, session);

        TerminalSessionStopResult result = await service.HandleExitAsync(
            session, 0, TimeSpan.Zero, _ => { }, Unwire, () => { });

        Assert.True(result.Applied);
        Assert.Equal(TerminalSessionStopErrorKind.Dispose, result.ErrorKind);
        Assert.Same(session.DisposeError, result.Error);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StopCallbackFailureStillDisposesDetachedSession(bool failUnwire)
    {
        var service = new TerminalSessionOrchestrator();
        var session = new FakeSession();
        await StartAsync(service, session);

        TerminalSessionStopResult result = await service.StopAsync(
            session,
            false,
            _ => { if (failUnwire) throw new InvalidOperationException("unwire"); },
            () => { if (!failUnwire) throw new InvalidOperationException("reset"); });

        Assert.True(result.Applied);
        Assert.NotNull(result.Error);
        Assert.Equal(TerminalSessionStopErrorKind.Callback, result.ErrorKind);
        Assert.Equal(1, session.DisposeCount);
        Assert.Null(service.Current);
    }

    private static Task<TerminalSessionStartResult> StartAsync(
        TerminalSessionOrchestrator service,
        FakeSession session) =>
        service.StartAsync(() => Task.FromResult<ITerminalSession>(session), Wire, Unwire, () => { }, () => false);

    private static void Wire(ITerminalSession session) { }
    private static void Unwire(ITerminalSession session) { }

    private sealed class FakeSession : ITerminalSession
    {
        public TerminalSessionCapabilities Capabilities { get; } = new(
            TerminalSessionKind.ConPty, SupportsResize: true, SupportsTerminalInput: true);
        public Exception? StartError { get; init; }
        public Exception? DisposeError { get; init; }
        public int StartCount { get; private set; }
        public int DisposeCount { get; private set; }
        public int ForceUnlockCount { get; private set; }
        public event EventHandler<string>? OutputReceived { add { } remove { } }
        public event EventHandler<int>? Exited { add { } remove { } }
        public void Start() { StartCount++; if (StartError is not null) throw StartError; }
        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return DisposeError is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(DisposeError);
        }
        public void Dispose() => DisposeCount++;
        public bool TryForceUnlock(uint exitCode = 1) { ForceUnlockCount++; return true; }
        public bool IsOutputStalled(TimeSpan initialOutputTimeout, TimeSpan idleOutputTimeout) => false;
        public void Resize(short columns, short rows) { }
        public void Write(string input) { }
        public void Write(byte[] input) { }
    }
}
