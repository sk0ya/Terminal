namespace Terminal.Tabs;

internal enum TerminalRenderAction
{
    None,
    RenderNow,
    Dispatch,
    StartThrottle
}

internal readonly record struct TerminalRenderDecision(
    TerminalRenderAction Action,
    TimeSpan ThrottleDelay = default,
    bool StopThrottle = false);

internal sealed class TerminalRenderCoordinator
{
    private DateTime _lastRenderUtc = DateTime.MinValue;
    private bool _renderScheduled;
    private bool _rendering;
    private bool _watchdogArmed;

    public bool IsRendering => _rendering;
    public bool IsRenderScheduled => _renderScheduled;
    public bool IsWatchdogArmed => _watchdogArmed;

    public TerminalRenderDecision RequestRender(
        bool immediate,
        bool dispatcherAccess,
        bool throttleTimerActive,
        DateTime nowUtc,
        TimeSpan minimumInterval)
    {
        if (immediate)
        {
            _renderScheduled = !dispatcherAccess;
            return new TerminalRenderDecision(
                dispatcherAccess ? TerminalRenderAction.RenderNow : TerminalRenderAction.Dispatch,
                StopThrottle: true);
        }

        if (_renderScheduled || throttleTimerActive)
        {
            return default;
        }

        TimeSpan elapsed = nowUtc - _lastRenderUtc;
        if (_lastRenderUtc == DateTime.MinValue || elapsed >= minimumInterval)
        {
            _renderScheduled = true;
            return new TerminalRenderDecision(TerminalRenderAction.Dispatch);
        }

        return new TerminalRenderDecision(
            TerminalRenderAction.StartThrottle,
            minimumInterval - elapsed);
    }

    public bool OnThrottleTick()
    {
        if (_renderScheduled)
        {
            return false;
        }

        _renderScheduled = true;
        return true;
    }

    public void BeginDispatchedRender()
    {
        _renderScheduled = false;
    }

    public bool TryBeginRender()
    {
        if (_rendering)
        {
            return false;
        }

        _rendering = true;
        return true;
    }

    public void EndRender(DateTime completedAtUtc)
    {
        _lastRenderUtc = completedAtUtc;
        _rendering = false;
    }

    public bool ArmWatchdog()
    {
        if (_watchdogArmed)
        {
            return false;
        }

        _watchdogArmed = true;
        return true;
    }

    public bool DisarmWatchdog()
    {
        bool wasArmed = _watchdogArmed;
        _watchdogArmed = false;
        return wasArmed;
    }

    public bool ConsumeWatchdogTick()
    {
        if (!_watchdogArmed)
        {
            return false;
        }

        _watchdogArmed = false;
        return true;
    }
}
