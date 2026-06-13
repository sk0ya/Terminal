namespace Terminal.Tabs;

/// <summary>
/// Phase of a shell command observed via OSC 133 shell integration
/// (the A/B/C/D markers emitted by an integrated shell).
/// </summary>
public enum ShellCommandPhase
{
    /// <summary>OSC 133;A — the shell printed a new prompt.</summary>
    PromptStart,

    /// <summary>OSC 133;B — the prompt ended and command-line input begins.</summary>
    CommandStart,

    /// <summary>OSC 133;C — the entered command started executing.</summary>
    CommandExecuted,

    /// <summary>OSC 133;D — the command finished (see <see cref="ShellCommandActivityEventArgs.ExitCode"/>).</summary>
    CommandDone
}

/// <summary>Event data for <see cref="TerminalTabView.ShellCommandActivity"/>.</summary>
public sealed class ShellCommandActivityEventArgs : EventArgs
{
    internal ShellCommandActivityEventArgs(ShellCommandPhase phase, int? exitCode)
    {
        Phase = phase;
        ExitCode = exitCode;
    }

    public ShellCommandPhase Phase { get; }

    /// <summary>
    /// Exit code reported with <see cref="ShellCommandPhase.CommandDone"/>;
    /// null for other phases or when the shell did not report one.
    /// </summary>
    public int? ExitCode { get; }
}
