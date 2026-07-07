using Terminal.Sessions;
using Terminal.Tabs;

namespace Terminal.Tests;

public sealed class TerminalSessionOrchestratorTests
{
    [Fact]
    public void CloseCanBeginOnlyOnce()
    {
        var service = new TerminalSessionOrchestrator();

        Assert.False(service.IsClosing);
        Assert.True(service.TryBeginClose());
        Assert.True(service.IsClosing);
        Assert.False(service.TryBeginClose());
    }

    [Fact]
    public async Task ConcurrentCloseHasSingleOwner()
    {
        var service = new TerminalSessionOrchestrator();
        var ready = new CountdownEvent(8);
        var release = new ManualResetEventSlim();
        Task<bool>[] attempts = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            ready.Signal();
            release.Wait();
            return service.TryBeginClose();
        })).ToArray();

        ready.Wait();
        release.Set();
        bool[] results = await Task.WhenAll(attempts);

        Assert.Single(results, result => result);
        Assert.True(service.IsClosing);
    }

    [Fact]
    public async Task StartAfterCloseDoesNotCreateSession()
    {
        var service = new TerminalSessionOrchestrator();
        int createCount = 0;
        int resetCount = 0;
        Assert.True(service.TryBeginClose());

        TerminalSessionStartResult result = await service.StartAsync(
            () =>
            {
                createCount++;
                return Task.FromResult<ITerminalSession>(new FakeSession());
            },
            Wire,
            Unwire,
            () => resetCount++);

        Assert.False(result.Started);
        Assert.Equal(0, createCount);
        Assert.Equal(1, resetCount);
        Assert.Null(service.Current);
    }

    [Fact]
    public async Task CloseDuringSessionCreationCleansStartedCandidate()
    {
        var service = new TerminalSessionOrchestrator();
        var session = new FakeSession();
        var createEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCreate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int wireCount = 0;
        int unwireCount = 0;
        Task<TerminalSessionStartResult> start = service.StartAsync(
            async () =>
            {
                createEntered.SetResult();
                await releaseCreate.Task;
                return session;
            },
            _ => wireCount++,
            _ => unwireCount++,
            () => { });
        await createEntered.Task;

        Assert.True(service.TryBeginClose());
        releaseCreate.SetResult();
        TerminalSessionStartResult result = await start;

        Assert.False(result.Started);
        Assert.Equal(1, wireCount);
        Assert.Equal(1, unwireCount);
        Assert.Equal(1, session.StartCount);
        Assert.Equal(1, session.DisposeCount);
        Assert.Null(service.Current);
    }

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
            () => { });

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
            () => throw new InvalidOperationException("reset"));

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

    [Fact]
    public async Task RecoveryRunsForceUnlockBeforeRestartAndResetsOwnership()
    {
        var service = new TerminalSessionOrchestrator();
        var order = new List<string>();
        var session = new FakeSession { ForceUnlockAction = () => order.Add("force") };
        await StartAsync(service, session);

        TerminalRecoveryResult result = await service.RecoverAsync(
            session, false, 1,
            () => order.Add("prepare"),
            () => { order.Add("restart"); return Task.CompletedTask; });

        Assert.Equal(TerminalRecoveryStatus.Completed, result.Status);
        Assert.Equal(["force", "prepare", "restart"], order);
        Assert.False(service.IsRecovering);
    }

    [Fact]
    public async Task AutomaticRecoveryHonorsAttemptLimitAndCanBeReset()
    {
        var service = new TerminalSessionOrchestrator();
        var session = new FakeSession();
        await StartAsync(service, session);

        Assert.Equal(TerminalRecoveryStatus.Completed, (await Recover(service, session, automatic: true)).Status);
        Assert.Equal(TerminalRecoveryStatus.LimitReached, (await Recover(service, session, automatic: true)).Status);
        Assert.Equal(1, session.ForceUnlockCount);

        service.ResetRecoveryAttempts();
        Assert.Equal(TerminalRecoveryStatus.Completed, (await Recover(service, session, automatic: true)).Status);
    }

    [Fact]
    public async Task ConcurrentRecoveryHasSingleOwnerAndFailureReleasesIt()
    {
        var service = new TerminalSessionOrchestrator();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new FakeSession
        {
            ForceUnlockAction = () =>
            {
                entered.SetResult();
                release.Task.GetAwaiter().GetResult();
            }
        };
        await StartAsync(service, session);

        Task<TerminalRecoveryResult> owner = Recover(service, session, automatic: false, restartError: true);
        await entered.Task;
        Assert.Equal(TerminalRecoveryStatus.Ignored, (await Recover(service, session, automatic: false)).Status);
        release.SetResult();
        Assert.Equal(TerminalRecoveryStatus.Failed, (await owner).Status);
        Assert.False(service.IsRecovering);

        session.ForceUnlockAction = null;
        Assert.Equal(TerminalRecoveryStatus.Completed, (await Recover(service, session, automatic: false)).Status);
    }

    [Fact]
    public async Task RecoveryRejectsStaleSessionAndClosingState()
    {
        var service = new TerminalSessionOrchestrator();
        var stale = new FakeSession();
        var current = new FakeSession();
        await StartAsync(service, stale);
        await StartAsync(service, current);

        Assert.Equal(TerminalRecoveryStatus.Ignored, (await Recover(service, stale, automatic: false)).Status);
        Assert.True(service.TryBeginClose());
        TerminalRecoveryResult closing = await service.RecoverAsync(
            current, false, 1, () => { }, () => Task.CompletedTask);
        Assert.Equal(TerminalRecoveryStatus.Ignored, closing.Status);
        Assert.Equal(0, current.ForceUnlockCount);
    }

    [Fact]
    public async Task CloseDuringRecoveryPreventsRestart()
    {
        var service = new TerminalSessionOrchestrator();
        var forceEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseForce = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new FakeSession
        {
            ForceUnlockAction = () =>
            {
                forceEntered.SetResult();
                releaseForce.Task.GetAwaiter().GetResult();
            }
        };
        await StartAsync(service, session);
        int prepareCount = 0;
        int restartCount = 0;
        Task<TerminalRecoveryResult> recovery = service.RecoverAsync(
            session,
            isAutomatic: false,
            maxAutomaticAttempts: 1,
            () => prepareCount++,
            () => { restartCount++; return Task.CompletedTask; });
        await forceEntered.Task;

        Assert.True(service.TryBeginClose());
        releaseForce.SetResult();
        TerminalRecoveryResult result = await recovery;

        Assert.Equal(TerminalRecoveryStatus.Ignored, result.Status);
        Assert.Equal(1, session.ForceUnlockCount);
        Assert.Equal(0, prepareCount);
        Assert.Equal(0, restartCount);
        Assert.False(service.IsRecovering);
    }

    [Fact]
    public async Task CloseWinningRestartAdmissionPreventsBothCallbacks()
    {
        var service = new TerminalSessionOrchestrator();
        var session = new FakeSession();
        await StartAsync(service, session);
        var admissionReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAdmission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int prepareCount = 0;
        int restartCount = 0;
        Task<TerminalRecoveryResult> recovery = service.RecoverAsync(
            session,
            isAutomatic: false,
            maxAutomaticAttempts: 1,
            () => prepareCount++,
            () => { restartCount++; return Task.CompletedTask; },
            () =>
            {
                admissionReached.SetResult();
                releaseAdmission.Task.GetAwaiter().GetResult();
            });
        await admissionReached.Task;

        Assert.True(service.TryBeginClose());
        releaseAdmission.SetResult();
        TerminalRecoveryResult result = await recovery;

        Assert.Equal(TerminalRecoveryStatus.Ignored, result.Status);
        Assert.Equal(0, prepareCount);
        Assert.Equal(0, restartCount);
    }

    [Fact]
    public async Task RecoveryWinningAdmissionStartsCallbacksBeforeCloseWithoutHoldingLockAcrossAwait()
    {
        var service = new TerminalSessionOrchestrator();
        var session = new FakeSession();
        await StartAsync(service, session);
        var prepareEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePrepare = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var restartEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRestart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<TerminalRecoveryResult> recovery = service.RecoverAsync(
            session,
            isAutomatic: false,
            maxAutomaticAttempts: 1,
            () =>
            {
                prepareEntered.SetResult();
                releasePrepare.Task.GetAwaiter().GetResult();
            },
            () =>
            {
                restartEntered.SetResult();
                return releaseRestart.Task;
            });
        await prepareEntered.Task;
        var closeAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> close = Task.Run(() =>
        {
            closeAttempted.SetResult();
            return service.TryBeginClose();
        });
        await closeAttempted.Task;
        Assert.False(close.IsCompleted);

        releasePrepare.SetResult();
        await restartEntered.Task;
        Assert.True(await close.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(recovery.IsCompleted);

        releaseRestart.SetResult();
        Assert.Equal(TerminalRecoveryStatus.Completed, (await recovery).Status);
    }

    [Fact]
    public async Task StalledRecoveryShortCircuitsWithoutCurrentSessionOrWhileClosing()
    {
        var service = new TerminalSessionOrchestrator();

        Assert.Equal(TerminalRecoveryStatus.Ignored, (await RecoverStalled(service)).Status);

        var session = new FakeSession { IsStalled = true };
        await StartAsync(service, session);
        Assert.True(service.TryBeginClose());
        Assert.Equal(TerminalRecoveryStatus.Ignored, (await RecoverStalled(service)).Status);
        Assert.Equal(0, session.StallProbeCount);
        Assert.Equal(0, session.ForceUnlockCount);
    }

    [Fact]
    public async Task StalledRecoveryDoesNotProbeWhileRecoveryIsActive()
    {
        var service = new TerminalSessionOrchestrator();
        var forceEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseForce = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new FakeSession
        {
            IsStalled = true,
            ForceUnlockAction = () =>
            {
                forceEntered.SetResult();
                releaseForce.Task.GetAwaiter().GetResult();
            }
        };
        await StartAsync(service, session);
        Task<TerminalRecoveryResult> recovery = Recover(service, session, automatic: false);
        await forceEntered.Task;

        Assert.Equal(TerminalRecoveryStatus.Ignored, (await RecoverStalled(service)).Status);
        Assert.Equal(0, session.StallProbeCount);

        releaseForce.SetResult();
        Assert.Equal(TerminalRecoveryStatus.Completed, (await recovery).Status);
    }

    [Fact]
    public async Task StalledRecoveryDoesNotProbeCurrentSessionDuringTransition()
    {
        var service = new TerminalSessionOrchestrator();
        var startEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new FakeSession
        {
            IsStalled = true,
            StartAction = () =>
            {
                startEntered.SetResult();
                releaseStart.Task.GetAwaiter().GetResult();
            }
        };
        Task<TerminalSessionStartResult> start = StartAsync(service, session);
        await startEntered.Task;
        Assert.Same(session, service.Current);

        Assert.Equal(TerminalRecoveryStatus.Ignored, (await RecoverStalled(service)).Status);
        Assert.Equal(0, session.StallProbeCount);

        releaseStart.SetResult();
        Assert.True((await start).Started);
    }

    [Fact]
    public async Task StalledRecoveryPassesTimeoutsAndIgnoresHealthySession()
    {
        var service = new TerminalSessionOrchestrator();
        var session = new FakeSession();
        await StartAsync(service, session);
        var initial = TimeSpan.FromSeconds(4);
        var idle = TimeSpan.FromSeconds(20);

        TerminalRecoveryResult result = await service.RecoverStalledAsync(
            initial, idle, 1, () => { }, () => Task.CompletedTask);

        Assert.Equal(TerminalRecoveryStatus.Ignored, result.Status);
        Assert.Equal(1, session.StallProbeCount);
        Assert.Equal(initial, session.LastInitialOutputTimeout);
        Assert.Equal(idle, session.LastIdleOutputTimeout);
        Assert.Equal(0, session.ForceUnlockCount);
    }

    [Fact]
    public async Task StalledRecoveryRunsAutomaticRecoveryAndHonorsAttemptLimit()
    {
        var service = new TerminalSessionOrchestrator();
        var order = new List<string>();
        var session = new FakeSession
        {
            IsStalled = true,
            ForceUnlockAction = () => order.Add("force")
        };
        await StartAsync(service, session);

        TerminalRecoveryResult first = await RecoverStalled(
            service,
            () => order.Add("prepare"),
            () => { order.Add("restart"); return Task.CompletedTask; });
        TerminalRecoveryResult second = await RecoverStalled(service);

        Assert.Equal(TerminalRecoveryStatus.Completed, first.Status);
        Assert.Equal(TerminalRecoveryStatus.LimitReached, second.Status);
        Assert.Equal(["force", "prepare", "restart"], order);
        Assert.Equal(2, session.StallProbeCount);
        Assert.Equal(1, session.ForceUnlockCount);
        Assert.Equal(1, service.AutoRecoveryAttempts);
    }

    [Fact]
    public async Task ConcurrentStalledRecoveryHasSingleRecoveryOwner()
    {
        var service = new TerminalSessionOrchestrator();
        var probesReady = new CountdownEvent(2);
        var releaseProbes = new ManualResetEventSlim();
        var forceEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseForce = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new FakeSession
        {
            IsStalled = true,
            StallProbeAction = () =>
            {
                probesReady.Signal();
                releaseProbes.Wait();
            },
            ForceUnlockAction = () =>
            {
                forceEntered.SetResult();
                releaseForce.Task.GetAwaiter().GetResult();
            }
        };
        await StartAsync(service, session);

        Task<TerminalRecoveryResult> first = Task.Run(() => RecoverStalled(service));
        Task<TerminalRecoveryResult> second = Task.Run(() => RecoverStalled(service));
        probesReady.Wait();
        releaseProbes.Set();
        await forceEntered.Task;
        Task<TerminalRecoveryResult> loser = await Task.WhenAny(first, second).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(TerminalRecoveryStatus.Ignored, (await loser).Status);
        releaseForce.SetResult();
        TerminalRecoveryResult[] results = await Task.WhenAll(first, second);

        Assert.Single(results, result => result.Status == TerminalRecoveryStatus.Completed);
        Assert.Single(results, result => result.Status == TerminalRecoveryStatus.Ignored);
        Assert.Equal(2, session.StallProbeCount);
        Assert.Equal(1, session.ForceUnlockCount);
    }

    [Fact]
    public async Task CloseStartingDuringStallProbePreventsRecoveryCallbacks()
    {
        var service = new TerminalSessionOrchestrator();
        var session = new FakeSession { IsStalled = true };
        await StartAsync(service, session);
        int prepareCount = 0;
        int restartCount = 0;

        TerminalRecoveryResult result = await service.RecoverStalledAsync(
            TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(20),
            1,
            () => prepareCount++,
            () => { restartCount++; return Task.CompletedTask; },
            () => Assert.True(service.TryBeginClose()));

        Assert.Equal(TerminalRecoveryStatus.Ignored, result.Status);
        Assert.Equal(0, session.ForceUnlockCount);
        Assert.Equal(0, prepareCount);
        Assert.Equal(0, restartCount);
    }

    [Fact]
    public async Task SessionTransitionStartingDuringStallProbePreventsRecoveryCallbacks()
    {
        var service = new TerminalSessionOrchestrator();
        var stalled = new FakeSession { IsStalled = true };
        var replacement = new FakeSession();
        await StartAsync(service, stalled);
        var createEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCreate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<TerminalSessionStartResult>? transition = null;
        int prepareCount = 0;
        int restartCount = 0;

        TerminalRecoveryResult result = await service.RecoverStalledAsync(
            TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(20),
            1,
            () => prepareCount++,
            () => { restartCount++; return Task.CompletedTask; },
            () => transition = service.StartAsync(
                async () =>
                {
                    createEntered.SetResult();
                    await releaseCreate.Task;
                    return replacement;
                },
                Wire,
                Unwire,
                () => { }));
        await createEntered.Task;

        Assert.Equal(TerminalRecoveryStatus.Ignored, result.Status);
        Assert.Equal(0, stalled.ForceUnlockCount);
        Assert.Equal(0, prepareCount);
        Assert.Equal(0, restartCount);

        releaseCreate.SetResult();
        Assert.True((await transition!).Started);
    }

    private static Task<TerminalRecoveryResult> Recover(
        TerminalSessionOrchestrator service,
        FakeSession session,
        bool automatic,
        bool restartError = false) =>
        service.RecoverAsync(
            session, automatic, 1, () => { },
            () => restartError
                ? Task.FromException(new InvalidOperationException("restart"))
                : Task.CompletedTask);

    private static Task<TerminalRecoveryResult> RecoverStalled(
        TerminalSessionOrchestrator service,
        Action? prepareRestart = null,
        Func<Task>? restart = null) =>
        service.RecoverStalledAsync(
            TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(20),
            1,
            prepareRestart ?? (() => { }),
            restart ?? (() => Task.CompletedTask));

    private static Task<TerminalSessionStartResult> StartAsync(
        TerminalSessionOrchestrator service,
        FakeSession session) =>
        service.StartAsync(() => Task.FromResult<ITerminalSession>(session), Wire, Unwire, () => { });

    private static void Wire(ITerminalSession session) { }
    private static void Unwire(ITerminalSession session) { }

    private sealed class FakeSession : ITerminalSession
    {
        public TerminalSessionCapabilities Capabilities { get; } = new(
            TerminalSessionKind.ConPty, SupportsResize: true, SupportsTerminalInput: true);
        public Exception? StartError { get; init; }
        public Exception? DisposeError { get; init; }
        public Action? ForceUnlockAction { get; set; }
        public Action? StartAction { get; set; }
        public Action? StallProbeAction { get; set; }
        public bool IsStalled { get; set; }
        public int StartCount { get; private set; }
        public int DisposeCount { get; private set; }
        public int ForceUnlockCount { get; private set; }
        public int StallProbeCount { get; private set; }
        public TimeSpan LastInitialOutputTimeout { get; private set; }
        public TimeSpan LastIdleOutputTimeout { get; private set; }
        public event EventHandler<string>? OutputReceived { add { } remove { } }
        public event EventHandler<int>? Exited { add { } remove { } }
        public void Start()
        {
            StartCount++;
            StartAction?.Invoke();
            if (StartError is not null) throw StartError;
        }
        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return DisposeError is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(DisposeError);
        }
        public void Dispose() => DisposeCount++;
        public bool TryForceUnlock(uint exitCode = 1)
        {
            ForceUnlockCount++;
            ForceUnlockAction?.Invoke();
            return true;
        }
        public bool IsOutputStalled(TimeSpan initialOutputTimeout, TimeSpan idleOutputTimeout)
        {
            StallProbeCount++;
            LastInitialOutputTimeout = initialOutputTimeout;
            LastIdleOutputTimeout = idleOutputTimeout;
            StallProbeAction?.Invoke();
            return IsStalled;
        }
        public void Resize(short columns, short rows) { }
        public void Write(string input) { }
        public void Write(byte[] input) { }
    }
}
