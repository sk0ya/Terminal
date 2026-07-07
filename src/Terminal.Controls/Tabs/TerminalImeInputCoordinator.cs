namespace Terminal.Tabs;

internal enum ImeCommitAction
{
    None,
    UpdateOverlay,
    ScheduleCommittedFlush
}

internal sealed class TerminalImeInputCoordinator
{
    public bool ImeCompositionActive { get; private set; }
    public bool HasPendingProxyText { get; private set; }
    public bool IsResettingProxyText { get; private set; }
    public bool DeferredFlushQueued { get; private set; }

    public bool ShouldUseProxyCaret =>
        ShouldUseProxyCaretForState(HasPendingProxyText, ImeCompositionActive);

    public void BeginOrUpdateComposition()
    {
        ImeCompositionActive = true;
    }

    public bool OnProxyTextChanged(bool hasPendingText)
    {
        HasPendingProxyText = hasPendingText;
        return !IsResettingProxyText;
    }

    public bool ShouldProcessSelectionChange() => !IsResettingProxyText;

    public ImeCommitAction Commit(bool hasPendingText)
    {
        HasPendingProxyText = hasPendingText;
        if (IsResettingProxyText)
        {
            return ImeCommitAction.None;
        }

        ImeCompositionActive = false;
        DeferredFlushQueued = false;
        return hasPendingText
            ? ImeCommitAction.ScheduleCommittedFlush
            : ImeCommitAction.UpdateOverlay;
    }

    public bool CanFlushProxyText() => !IsResettingProxyText && HasPendingProxyText;

    public bool TryQueueDeferredFlush()
    {
        if (DeferredFlushQueued)
        {
            return false;
        }

        DeferredFlushQueued = true;
        return true;
    }

    public bool TryConsumeDeferredFlush()
    {
        if (!DeferredFlushQueued)
        {
            return false;
        }

        DeferredFlushQueued = false;
        return true;
    }

    public void BeginReset()
    {
        ImeCompositionActive = false;
        DeferredFlushQueued = false;
        HasPendingProxyText = false;
        IsResettingProxyText = true;
    }

    public void EndReset()
    {
        IsResettingProxyText = false;
    }

    public static bool ShouldUseProxyCaretForState(bool hasPendingProxyText, bool imeCompositionActive) =>
        hasPendingProxyText && imeCompositionActive;
}
