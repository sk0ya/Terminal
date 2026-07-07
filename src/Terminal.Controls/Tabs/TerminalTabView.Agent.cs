using Terminal.Buffer;

namespace Terminal.Tabs;

/// <summary>
/// Result of <see cref="TerminalTabView.RunCommandAsync"/>: the command that was sent,
/// its plain-text output, the exit code (-1 when unavailable), and whether completion
/// was detected (false on timeout, cancellation, or when no completion mechanism exists).
/// </summary>
public readonly record struct TerminalCommandResult(
    string Command,
    string Output,
    int ExitCode,
    bool Completed);

public partial class TerminalTabView
{
    private static readonly TimeSpan AgentCommandTimeout = TimeSpan.FromMinutes(10);

    private readonly SemaphoreSlim _runCommandGate = new(1, 1);
    private AgentCommandExecution? _activeAgentCommand;
    private bool _shellIntegrationObserved;

    /// <summary>
    /// True once OSC 133 shell-integration markers have been observed on this session,
    /// meaning <see cref="RunCommandAsync"/> can detect command completion precisely.
    /// When false, a sentinel fallback is used for known shells.
    /// </summary>
    public bool IsShellIntegrationActive => _shellIntegrationObserved;

    /// <summary>
    /// Sends a command to the interactive shell, waits for it to finish, and returns the
    /// captured output and exit code. The command runs and is displayed on the live PTY
    /// exactly as if typed. Calls are serialized; concurrent callers are queued.
    /// </summary>
    public Task<TerminalCommandResult> RunCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        return Dispatcher.CheckAccess()
            ? RunCommandGatedAsync(command, cancellationToken)
            : Dispatcher.InvokeAsync(() => RunCommandGatedAsync(command, cancellationToken)).Task.Unwrap();
    }

    private async Task<TerminalCommandResult> RunCommandGatedAsync(string command, CancellationToken cancellationToken)
    {
        await _runCommandGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            return await ExecuteAgentCommandAsync(command, cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            _runCommandGate.Release();
        }
    }

    private async Task<TerminalCommandResult> ExecuteAgentCommandAsync(string command, CancellationToken cancellationToken)
    {
        if (_session is null || cancellationToken.IsCancellationRequested)
        {
            return new TerminalCommandResult(command, string.Empty, -1, false);
        }

        bool useShellIntegration = IsShellIntegrationActive;
        var execution = new AgentCommandExecution(command, useShellIntegration);

        string lineToSend;
        if (useShellIntegration)
        {
            lineToSend = command;
        }
        else
        {
            AgentShellKind shell = AgentCommandProtocol.DetectShellKind(_launchState.ActiveCommandLine);
            if (shell == AgentShellKind.Unknown)
            {
                // No completion mechanism available: report not-completed rather than hang.
                return new TerminalCommandResult(command, string.Empty, -1, false);
            }

            execution.MarkerId = AgentCommandProtocol.NewMarkerId();
            lineToSend = AgentCommandProtocol.BuildSentinelCommand(shell, command, execution.MarkerId);
            // Record where output will begin so a cancelled/timed-out sentinel command can
            // still return the text produced so far (the OSC path records this at marker C).
            execution.OutputStartLine = _terminalBuffer.ScrollbackLineCount + _terminalBuffer.CursorRow;
        }

        _activeAgentCommand = execution;

        using CancellationTokenRegistration cancellationRegistration = cancellationToken.Register(() =>
            _ = Dispatcher.BeginInvoke(() => CancelAgentCommand(execution)));
        using var timeoutCts = new CancellationTokenSource(AgentCommandTimeout);
        using CancellationTokenRegistration timeoutRegistration = timeoutCts.Token.Register(() =>
            _ = Dispatcher.BeginInvoke(() => TimeoutAgentCommand(execution)));

        if (!SendTerminalInput(lineToSend + "\r"))
        {
            if (ReferenceEquals(_activeAgentCommand, execution))
            {
                _activeAgentCommand = null;
            }

            return new TerminalCommandResult(command, string.Empty, -1, false);
        }

        return await execution.Completion.Task.ConfigureAwait(true);
    }

    /// <summary>
    /// Completes any pending command as not-completed when the session goes away, so an
    /// in-flight <see cref="RunCommandAsync"/> caller does not wait for the full timeout.
    /// </summary>
    private void AbortActiveAgentCommand()
    {
        AgentCommandExecution? execution = _activeAgentCommand;
        if (execution is null)
        {
            return;
        }

        CompleteAgentCommand(
            execution,
            new TerminalCommandResult(execution.Command, CaptureCurrentOutput(execution), -1, false));
    }

    private void CompleteAgentCommand(AgentCommandExecution execution, TerminalCommandResult result)
    {
        if (!ReferenceEquals(_activeAgentCommand, execution))
        {
            return;
        }

        _activeAgentCommand = null;
        execution.Completion.TrySetResult(result);
    }

    private void CancelAgentCommand(AgentCommandExecution execution)
    {
        if (!ReferenceEquals(_activeAgentCommand, execution))
        {
            return;
        }

        SendInterrupt();
        CompleteAgentCommand(
            execution,
            new TerminalCommandResult(execution.Command, CaptureCurrentOutput(execution), -1, false));
    }

    private void TimeoutAgentCommand(AgentCommandExecution execution)
    {
        if (!ReferenceEquals(_activeAgentCommand, execution))
        {
            return;
        }

        CompleteAgentCommand(
            execution,
            new TerminalCommandResult(execution.Command, CaptureCurrentOutput(execution), -1, false));
    }

    /// <summary>
    /// Best-effort capture of the output produced so far, used when a command is aborted,
    /// cancelled, or times out before its completion marker arrives.
    /// </summary>
    private string CaptureCurrentOutput(AgentCommandExecution execution)
    {
        if (execution.OutputStartLine < 0)
        {
            return string.Empty;
        }

        return _terminalBuffer.GetPlainTextForAbsoluteLineRange(execution.OutputStartLine, int.MaxValue);
    }

    /// <summary>
    /// Driven from the OSC 133 shell-zone handler to capture the output region (C..D) and
    /// exit code for the active shell-integration command.
    /// </summary>
    private void OnAgentShellCommandZone(ShellCommandZoneEventArgs e)
    {
        AgentCommandExecution? execution = _activeAgentCommand;
        if (execution is null || !execution.UseShellIntegration)
        {
            return;
        }

        if (e.ZoneType == ShellCommandZoneType.CommandExecuted)
        {
            execution.OutputStartLine = e.AbsoluteLine;
            execution.CommandExecutedSeen = true;
        }
        else if (e.ZoneType == ShellCommandZoneType.CommandDone && execution.CommandExecutedSeen)
        {
            int exitCode = e.ExitCode ?? -1;
            string output = _terminalBuffer.GetPlainTextForAbsoluteLineRange(execution.OutputStartLine, e.AbsoluteLine);
            CompleteAgentCommand(execution, new TerminalCommandResult(execution.Command, output, exitCode, true));
        }
    }

    /// <summary>
    /// Driven after each buffer flush to detect the sentinel BEGIN/END markers in the
    /// rendered terminal text for the active fallback command.
    /// </summary>
    private void TryCompleteAgentSentinel()
    {
        AgentCommandExecution? execution = _activeAgentCommand;
        if (execution is null || execution.UseShellIntegration)
        {
            return;
        }

        string snapshot = _terminalBuffer.CreatePlainTextSnapshot();
        if (AgentCommandProtocol.TryParseCompletedOutput(snapshot, execution.MarkerId, out string output, out int exitCode))
        {
            CompleteAgentCommand(execution, new TerminalCommandResult(execution.Command, output, exitCode, true));
        }
    }

    private sealed class AgentCommandExecution(string command, bool useShellIntegration)
    {
        public string Command { get; } = command;
        public bool UseShellIntegration { get; } = useShellIntegration;
        public TaskCompletionSource<TerminalCommandResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int OutputStartLine { get; set; } = -1;
        public bool CommandExecutedSeen { get; set; }
        public string MarkerId { get; set; } = string.Empty;
    }
}
