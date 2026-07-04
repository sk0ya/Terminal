using System.Runtime.CompilerServices;

using Terminal.Sessions;

namespace Terminal.Tabs;

internal sealed class TerminalSessionLifecycleCoordinator
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConditionalWeakTable<ITerminalSession, DisposeClaim> _disposeClaims = new();
    private long _generation;
    private long? _exitOwnerGeneration;

    public ITerminalSession? Current { get; private set; }
    public long Generation => _generation;
    public bool IsTransitionActive { get; private set; }

    public async Task BeginTransitionAsync()
    {
        await _gate.WaitAsync();
        IsTransitionActive = true;
    }

    public void EndTransition()
    {
        IsTransitionActive = false;
        _gate.Release();
    }

    public long Attach(ITerminalSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        Current = session;
        _generation++;
        _exitOwnerGeneration = null;
        return _generation;
    }

    public ITerminalSession? DetachCurrent()
    {
        ITerminalSession? session = Current;
        Current = null;
        _exitOwnerGeneration = null;
        return session;
    }

    public bool IsCurrent(ITerminalSession session) => ReferenceEquals(session, Current);

    public bool MatchesExpected(ITerminalSession? expected) =>
        expected is null || ReferenceEquals(expected, Current);

    public bool TryClaimExit(ITerminalSession session, out long generation)
    {
        generation = _generation;
        if (!ReferenceEquals(session, Current) || _exitOwnerGeneration == _generation)
        {
            return false;
        }

        _exitOwnerGeneration = _generation;
        return true;
    }

    public bool ShouldContinueExit(ITerminalSession session, long generation) =>
        ReferenceEquals(session, Current) &&
        generation == _generation &&
        _exitOwnerGeneration == generation;

    public bool TryClaimDisposal(ITerminalSession session)
    {
        DisposeClaim claim = _disposeClaims.GetValue(session, static _ => new DisposeClaim());
        return Interlocked.Exchange(ref claim.IsClaimed, 1) == 0;
    }

    private sealed class DisposeClaim
    {
        public int IsClaimed;
    }
}
