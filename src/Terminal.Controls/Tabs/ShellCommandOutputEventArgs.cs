namespace Terminal.Tabs;

/// <summary>Event data for <see cref="TerminalTabView.CommandOutputCaptured"/>.</summary>
public sealed class ShellCommandOutputEventArgs : EventArgs
{
    internal ShellCommandOutputEventArgs(string? commandLine, int? exitCode, string output, bool headLost)
    {
        CommandLine = commandLine;
        ExitCode = exitCode;
        Output = output;
        HeadLost = headLost;
    }

    /// <summary>The command line as reported via OSC 633;E (null when the shell did not report one).</summary>
    public string? CommandLine { get; }

    /// <summary>The exit code reported with OSC 133;D (null when the shell did not report one).</summary>
    public int? ExitCode { get; }

    /// <summary>
    /// The plain text the command printed, one line per terminal row (soft-wrapped lines come out as
    /// separate lines), joined with <see cref="Environment.NewLine"/>, with blank lines at either end
    /// dropped. Output that ended without a newline shares its row with the next prompt and is not
    /// included; a full-screen (alternate screen) program contributes only what it left on the
    /// primary screen.
    /// </summary>
    public string Output { get; }

    /// <summary>True when the head of the output had already scrolled out of the scrollback limit,
    /// so <see cref="Output"/> starts at the oldest line still kept.</summary>
    public bool HeadLost { get; }
}
