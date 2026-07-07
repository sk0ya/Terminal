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

    private readonly TerminalAgentCommandCoordinator _agentCommands = new();
    private readonly TerminalAgentCommandOrchestrator _agentCommandOrchestrator;

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
            ? _agentCommandOrchestrator.RunAsync(command, cancellationToken)
            : Dispatcher.InvokeAsync(
                () => _agentCommandOrchestrator.RunAsync(command, cancellationToken)).Task.Unwrap();
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
        _agentCommandOrchestrator.Cancel(execution);
    }

    private void TimeoutAgentCommand(TerminalAgentCommandExecution execution)
    {
        _agentCommandOrchestrator.Timeout(execution);
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
