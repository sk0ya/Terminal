namespace Terminal.Tabs;

internal readonly record struct TerminalTaskbarProgress(
    TaskbarProgressState State,
    int Progress);

internal sealed class TerminalTaskbarProgressCoordinator
{
    public TerminalTaskbarProgress Current { get; private set; } =
        new(TaskbarProgressState.None, 0);

    public TerminalTaskbarProgress ApplyOscProgress(int stateCode, int progress)
    {
        TaskbarProgressState state = stateCode switch
        {
            1 => TaskbarProgressState.Normal,
            2 => TaskbarProgressState.Error,
            3 => TaskbarProgressState.Indeterminate,
            4 => TaskbarProgressState.Warning,
            _ => TaskbarProgressState.None
        };

        int effectiveProgress = state is TaskbarProgressState.Indeterminate or TaskbarProgressState.None
            ? 0
            : progress;
        return Set(state, effectiveProgress);
    }

    public TerminalTaskbarProgress Clear() => Set(TaskbarProgressState.None, 0);

    private TerminalTaskbarProgress Set(TaskbarProgressState state, int progress)
    {
        Current = new TerminalTaskbarProgress(state, progress);
        return Current;
    }
}
