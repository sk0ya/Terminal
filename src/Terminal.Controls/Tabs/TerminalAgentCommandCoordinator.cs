using Terminal.Buffer;

namespace Terminal.Tabs;

internal sealed class TerminalAgentCommandExecution(
    string command,
    bool useShellIntegration,
    string markerId,
    int outputStartLine)
{
    public string Command { get; } = command;
    public bool UseShellIntegration { get; } = useShellIntegration;
    public string MarkerId { get; } = markerId;
    public int OutputStartLine { get; set; } = outputStartLine;
    public bool CommandExecutedSeen { get; set; }
    public TaskCompletionSource<TerminalCommandResult> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class TerminalAgentCommandCoordinator
{
    private TerminalAgentCommandExecution? _current;

    public bool ShellIntegrationObserved { get; private set; }
    public TerminalAgentCommandExecution? Current => _current;

    public TerminalAgentCommandExecution Begin(string command, string markerId, int fallbackOutputStartLine)
    {
        bool useShellIntegration = ShellIntegrationObserved;
        var execution = new TerminalAgentCommandExecution(
            command,
            useShellIntegration,
            useShellIntegration ? string.Empty : markerId,
            useShellIntegration ? -1 : fallbackOutputStartLine);
        _current = execution;
        return execution;
    }

    public void ResetSession()
    {
        ShellIntegrationObserved = false;
    }

    public bool OnShellZone(ShellCommandZoneEventArgs e, Func<int, int, string> captureRange)
    {
        ShellIntegrationObserved = true;
        TerminalAgentCommandExecution? execution = _current;
        if (execution is null || !execution.UseShellIntegration)
        {
            return false;
        }

        if (e.ZoneType == ShellCommandZoneType.CommandExecuted)
        {
            execution.OutputStartLine = e.AbsoluteLine;
            execution.CommandExecutedSeen = true;
            return false;
        }

        if (e.ZoneType != ShellCommandZoneType.CommandDone || !execution.CommandExecutedSeen)
        {
            return false;
        }

        string output = captureRange(execution.OutputStartLine, e.AbsoluteLine);
        return TryComplete(
            execution,
            new TerminalCommandResult(execution.Command, output, e.ExitCode ?? -1, true));
    }

    public bool TryCompleteSentinel(string snapshot)
    {
        TerminalAgentCommandExecution? execution = _current;
        if (execution is null || execution.UseShellIntegration ||
            !AgentCommandProtocol.TryParseCompletedOutput(
                snapshot,
                execution.MarkerId,
                out string output,
                out int exitCode))
        {
            return false;
        }

        return TryComplete(
            execution,
            new TerminalCommandResult(execution.Command, output, exitCode, true));
    }

    public bool Cancel(
        TerminalAgentCommandExecution execution,
        Action interrupt,
        Func<int, string> captureFrom)
    {
        if (!ReferenceEquals(_current, execution))
        {
            return false;
        }

        interrupt();
        return CompleteIncomplete(execution, captureFrom);
    }

    public bool Timeout(TerminalAgentCommandExecution execution, Func<int, string> captureFrom) =>
        ReferenceEquals(_current, execution) && CompleteIncomplete(execution, captureFrom);

    public bool Abort(Func<int, string> captureFrom)
    {
        TerminalAgentCommandExecution? execution = _current;
        return execution is not null && CompleteIncomplete(execution, captureFrom);
    }

    public bool Abandon(TerminalAgentCommandExecution execution) =>
        ReferenceEquals(_current, execution) && ClearCurrent(execution);

    private bool CompleteIncomplete(
        TerminalAgentCommandExecution execution,
        Func<int, string> captureFrom)
    {
        string output = execution.OutputStartLine < 0
            ? string.Empty
            : captureFrom(execution.OutputStartLine);
        return TryComplete(
            execution,
            new TerminalCommandResult(execution.Command, output, -1, false));
    }

    private bool TryComplete(TerminalAgentCommandExecution execution, TerminalCommandResult result)
    {
        if (!ReferenceEquals(_current, execution))
        {
            return false;
        }

        _current = null;
        return execution.Completion.TrySetResult(result);
    }

    private bool ClearCurrent(TerminalAgentCommandExecution execution)
    {
        _current = null;
        return true;
    }
}
