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
    internal ShellCommandActivityEventArgs(
        ShellCommandPhase phase,
        int? exitCode,
        string? commandLine = null,
        TimeSpan? duration = null)
    {
        Phase = phase;
        ExitCode = exitCode;
        CommandLine = commandLine;
        Duration = duration;
    }

    public ShellCommandPhase Phase { get; }

    /// <summary>
    /// Exit code reported with <see cref="ShellCommandPhase.CommandDone"/>;
    /// null for other phases or when the shell did not report one.
    /// </summary>
    public int? ExitCode { get; }

    /// <summary>
    /// The command line the shell reported via OSC 633;E for the command this marker belongs
    /// to, as typed (not deduplicated, so a command repeated back-to-back is reported every
    /// time — unlike <see cref="TerminalTabView.CommandHistoryRecorded"/>). Set for
    /// <see cref="ShellCommandPhase.CommandExecuted"/> and <see cref="ShellCommandPhase.CommandDone"/>;
    /// null for the other phases, for an empty submission, or when the shell does not report it.
    /// </summary>
    public string? CommandLine { get; }

    /// <summary>
    /// How long the command ran, from OSC 133;C to OSC 133;D as observed by the terminal. Set for
    /// <see cref="ShellCommandPhase.CommandDone"/> when the command was seen to start; null otherwise.
    /// </summary>
    public TimeSpan? Duration { get; }
}
