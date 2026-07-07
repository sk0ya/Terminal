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
    private readonly TerminalAgentCommandCoordinator _agentCommands = new();

    /// <summary>
    /// True once OSC 133 shell-integration markers have been observed on this session,
    /// meaning <see cref="RunCommandAsync"/> can detect command completion precisely.
    /// When false, a sentinel fallback is used for known shells.
    /// </summary>
    public bool IsShellIntegrationActive => _agentCommands.ShellIntegrationObserved;

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

        bool useShellIntegration = _agentCommands.ShellIntegrationObserved;
        string lineToSend;
        string markerId = string.Empty;
        int outputStartLine = -1;
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

            markerId = AgentCommandProtocol.NewMarkerId();
            lineToSend = AgentCommandProtocol.BuildSentinelCommand(shell, command, markerId);
            // Record where output will begin so a cancelled/timed-out sentinel command can
            // still return the text produced so far (the OSC path records this at marker C).
            outputStartLine = _terminalBuffer.ScrollbackLineCount + _terminalBuffer.CursorRow;
        }

        TerminalAgentCommandExecution execution = _agentCommands.Begin(command, markerId, outputStartLine);

        using CancellationTokenRegistration cancellationRegistration = cancellationToken.Register(() =>
            _ = Dispatcher.BeginInvoke(() => CancelAgentCommand(execution)));
        using var timeoutCts = new CancellationTokenSource(AgentCommandTimeout);
        using CancellationTokenRegistration timeoutRegistration = timeoutCts.Token.Register(() =>
            _ = Dispatcher.BeginInvoke(() => TimeoutAgentCommand(execution)));

        if (!SendTerminalInput(lineToSend + "\r"))
        {
            _agentCommands.Abandon(execution);
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
        _agentCommands.Abort(startLine =>
            _terminalBuffer.GetPlainTextForAbsoluteLineRange(startLine, int.MaxValue));
    }

    private void CancelAgentCommand(TerminalAgentCommandExecution execution)
    {
        _agentCommands.Cancel(
            execution,
            SendInterrupt,
            startLine => _terminalBuffer.GetPlainTextForAbsoluteLineRange(startLine, int.MaxValue));
    }

    private void TimeoutAgentCommand(TerminalAgentCommandExecution execution)
    {
        _agentCommands.Timeout(
            execution,
            startLine => _terminalBuffer.GetPlainTextForAbsoluteLineRange(startLine, int.MaxValue));
    }

    /// <summary>
    /// Driven from the OSC 133 shell-zone handler to capture the output region (C..D) and
    /// exit code for the active shell-integration command.
    /// </summary>
    private void OnAgentShellCommandZone(ShellCommandZoneEventArgs e)
    {
        _agentCommands.OnShellZone(
            e,
            (startLine, endLine) =>
                _terminalBuffer.GetPlainTextForAbsoluteLineRange(startLine, endLine));
    }

    /// <summary>
    /// Driven after each buffer flush to detect the sentinel BEGIN/END markers in the
    /// rendered terminal text for the active fallback command.
    /// </summary>
    private void TryCompleteAgentSentinel()
    {
        _agentCommands.TryCompleteSentinel(_terminalBuffer.CreatePlainTextSnapshot());
    }
}
