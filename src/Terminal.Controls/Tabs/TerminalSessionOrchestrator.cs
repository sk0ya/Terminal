using Terminal.Sessions;

namespace Terminal.Tabs;

internal sealed record TerminalSessionStartResult(
    bool Started,
    Exception? Error = null,
    Exception? CleanupError = null,
    Exception? PreviousCleanupError = null);

internal enum TerminalSessionStopErrorKind
{
    None,
    Callback,
    ForceUnlock,
    Dispose
}

internal sealed record TerminalSessionStopResult(
    bool Applied,
    Exception? Error = null,
    TerminalSessionStopErrorKind ErrorKind = TerminalSessionStopErrorKind.None);

internal enum TerminalRecoveryStatus
{
    Completed,
    Ignored,
    LimitReached,
    Failed
}

internal sealed record TerminalRecoveryResult(TerminalRecoveryStatus Status, Exception? Error = null);

internal sealed class TerminalSessionOrchestrator
{
    private readonly TerminalSessionLifecycleCoordinator _lifecycle = new();
    private int _isRecovering;
    private int _autoRecoveryAttempts;

    public ITerminalSession? Current => _lifecycle.Current;
    public bool IsTransitionActive => _lifecycle.IsTransitionActive;
    public bool IsCurrent(ITerminalSession session) => _lifecycle.IsCurrent(session);
    public bool IsRecovering => Volatile.Read(ref _isRecovering) != 0;
    public int AutoRecoveryAttempts => Volatile.Read(ref _autoRecoveryAttempts);

    public void ResetRecoveryAttempts() => Interlocked.Exchange(ref _autoRecoveryAttempts, 0);

    public async Task<TerminalRecoveryResult> RecoverAsync(
        ITerminalSession? session,
        bool isAutomatic,
        int maxAutomaticAttempts,
        Func<bool> isClosing,
        Action prepareRestart,
        Func<Task> restart)
    {
        if (session is null || !_lifecycle.IsCurrent(session) || isClosing())
        {
            return new(TerminalRecoveryStatus.Ignored);
        }

        if (Interlocked.CompareExchange(ref _isRecovering, 1, 0) != 0)
        {
            return new(TerminalRecoveryStatus.Ignored);
        }

        try
        {
            if (isAutomatic)
            {
                if (AutoRecoveryAttempts >= maxAutomaticAttempts)
                {
                    return new(TerminalRecoveryStatus.LimitReached);
                }

                Interlocked.Increment(ref _autoRecoveryAttempts);
            }

            _ = await Task.Run(() => session.TryForceUnlock());
            if (!_lifecycle.IsCurrent(session) || isClosing())
            {
                return new(TerminalRecoveryStatus.Ignored);
            }

            prepareRestart();
            await restart();
            return new(TerminalRecoveryStatus.Completed);
        }
        catch (Exception ex)
        {
            return new(TerminalRecoveryStatus.Failed, ex);
        }
        finally
        {
            Volatile.Write(ref _isRecovering, 0);
        }
    }

    public async Task<TerminalSessionStartResult> StartAsync(
        Func<Task<ITerminalSession>> createSession,
        Action<ITerminalSession> wireEvents,
        Action<ITerminalSession> unwireEvents,
        Action resetView,
        Func<bool> isClosing)
    {
        await _lifecycle.BeginTransitionAsync();
        try
        {
            ITerminalSession? previous = _lifecycle.DetachCurrent();
            Exception? previousCleanupError = TryInvoke(previous, unwireEvents);
            previousCleanupError = Combine(previousCleanupError, await DisposeAsync(previous));
            ITerminalSession? candidate = null;
            bool attached = false;
            try
            {
                resetView();
                if (isClosing())
                {
                    return new(false, PreviousCleanupError: previousCleanupError);
                }

                candidate = await createSession();
                wireEvents(candidate);
                _lifecycle.Attach(candidate);
                attached = true;
                await Task.Run(candidate.Start);
                if (isClosing())
                {
                    Exception? cleanupError = await CleanupCandidateAsync(candidate, attached, unwireEvents);
                    return new(false, CleanupError: cleanupError, PreviousCleanupError: previousCleanupError);
                }

                return new(true, PreviousCleanupError: previousCleanupError);
            }
            catch (Exception error)
            {
                Exception? cleanupError = await CleanupCandidateAsync(candidate, attached, unwireEvents);
                return new(false, error, cleanupError, previousCleanupError);
            }
        }
        finally
        {
            _lifecycle.EndTransition();
        }
    }

    public async Task<TerminalSessionStopResult> StopAsync(
        ITerminalSession? expectedSession,
        bool forceTerminate,
        Action<ITerminalSession> unwireEvents,
        Action resetView)
    {
        await _lifecycle.BeginTransitionAsync();
        try
        {
            if (!_lifecycle.MatchesExpected(expectedSession))
            {
                return new(false);
            }

            ITerminalSession? session = _lifecycle.DetachCurrent();
            Exception? error = TryInvoke(session, unwireEvents);
            TerminalSessionStopErrorKind errorKind = error is null
                ? TerminalSessionStopErrorKind.None
                : TerminalSessionStopErrorKind.Callback;
            try
            {
                resetView();
            }
            catch (Exception ex)
            {
                error = Combine(error, ex);
                errorKind = TerminalSessionStopErrorKind.Callback;
            }

            if (forceTerminate && session is not null)
            {
                try
                {
                    _ = await Task.Run(() => session.TryForceUnlock());
                }
                catch (Exception ex)
                {
                    error = Combine(error, ex);
                    errorKind = TerminalSessionStopErrorKind.ForceUnlock;
                }
            }

            Exception? disposeError = await DisposeAsync(session);
            error = Combine(error, disposeError);
            if (disposeError is not null)
            {
                errorKind = TerminalSessionStopErrorKind.Dispose;
            }

            return new(true, error, errorKind);
        }
        finally
        {
            _lifecycle.EndTransition();
        }
    }

    public async Task<TerminalSessionStopResult> HandleExitAsync(
        ITerminalSession session,
        int drainPasses,
        TimeSpan drainInterval,
        Action<bool> flushOutput,
        Action<ITerminalSession> unwireEvents,
        Action resetView,
        Func<TimeSpan, Task>? delay = null)
    {
        try
        {
            if (!_lifecycle.TryClaimExit(session, out long generation))
            {
                return new(false);
            }

            delay ??= Task.Delay;
            for (int pass = 0; pass < drainPasses; pass++)
            {
                if (!_lifecycle.ShouldContinueExit(session, generation))
                {
                    return new(false);
                }

                flushOutput(false);
                await delay(drainInterval);
            }

            if (!_lifecycle.ShouldContinueExit(session, generation))
            {
                return new(false);
            }

            flushOutput(true);
            return await StopAsync(session, false, unwireEvents, resetView);
        }
        catch (Exception ex)
        {
            return new(false, ex, TerminalSessionStopErrorKind.Callback);
        }
    }

    private async Task<Exception?> CleanupCandidateAsync(
        ITerminalSession? candidate,
        bool attached,
        Action<ITerminalSession> unwireEvents)
    {
        if (candidate is null)
        {
            return null;
        }

        if (attached && _lifecycle.IsCurrent(candidate))
        {
            _ = _lifecycle.DetachCurrent();
        }

        Exception? error = TryInvoke(candidate, unwireEvents);
        return Combine(error, await DisposeAsync(candidate));
    }

    private static Exception? TryInvoke(
        ITerminalSession? session,
        Action<ITerminalSession> callback)
    {
        if (session is null)
        {
            return null;
        }

        try
        {
            callback(session);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static Exception? Combine(Exception? first, Exception? second) =>
        first is null ? second : second is null ? first : new AggregateException(first, second);

    private async Task<Exception?> DisposeAsync(ITerminalSession? session)
    {
        if (session is null || !_lifecycle.TryClaimDisposal(session))
        {
            return null;
        }

        try
        {
            await Task.Run(() => session.DisposeAsync().AsTask());
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }
}
