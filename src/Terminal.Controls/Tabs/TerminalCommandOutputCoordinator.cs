using Terminal.Buffer;

namespace Terminal.Tabs;

/// <summary>
/// Cuts one command's output out of the buffer for <see cref="TerminalTabView.CommandOutputCaptured"/>.
/// <para>
/// ConPTY (in its normal, non-passthrough mode) forwards the OSC 133 markers the moment the shell
/// writes them, but paints ordinary text on its own frame schedule. So the markers arrive <b>ahead</b>
/// of the text they belong to: D (and the next A/B) typically lands before the last lines of output and
/// the new prompt have been painted, and C can land before the newline that ends the echoed command
/// line. Capturing at D therefore loses the tail of the output, and starting at C can include the
/// echoed command. This coordinator instead:
/// </para>
/// <list type="bullet">
/// <item>waits after D until output has been quiet for a moment (the host drives that with a timer),
/// then ends the range where the new prompt starts: the cursor's row, walked back over the prompt's
/// extra lines (a multi-line prompt), never above the new prompt's A;</item>
/// <item>if the next command's C arrives first (pasted lines run back to back), ends at that C and
/// trims a trailing echo of the next command;</item>
/// <item>starts at C and drops a leading echo of the command line (the prompt row it was typed on).</item>
/// </list>
/// </summary>
internal sealed class TerminalCommandOutputCoordinator
{
    /// <summary>How long output must stay quiet after D before the range is cut.</summary>
    public static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>How many rows an echoed command line (prompt + command, soft-wrapped) may span.</summary>
    private const int MaxEchoRows = 6;

    private TerminalLineMark? _start;
    private string? _startCommandLine;
    private Pending? _pending;
    private TerminalLineMark? _promptStart;
    private bool _promptStartIsSettled;

    private sealed record Pending(TerminalLineMark Start, string? CommandLine, int? ExitCode);

    /// <summary>True between a D and the moment its output is cut.</summary>
    public bool HasPending => _pending is not null;

    /// <summary>
    /// How many lines a prompt spans above the line the command is typed on (1 for a two-line
    /// prompt). Measured from A to C, but only for a prompt whose A arrived while no command output
    /// was still being painted - after a command, A lands ahead of that output's tail, so the
    /// distance would overcount and the settle step would cut real output.
    /// </summary>
    public int PromptExtraLines { get; private set; }

    public void OnPromptStart(TerminalLineMark mark)
    {
        _promptStart = mark;
        _promptStartIsSettled = _pending is null && _start is null;
    }

    public void OnCommandExecuted(AnsiTerminalBuffer buffer, int absoluteLine, string? commandLine)
    {
        if (_promptStartIsSettled
            && _promptStart is { } promptStart
            && buffer.TryResolveMark(promptStart, out int promptLine)
            && promptLine <= absoluteLine
            && FindEchoRow(buffer, promptLine, absoluteLine, commandLine) is { } echoRow)
        {
            PromptExtraLines = buffer.CountHardLineBreaks(promptLine, buffer.WrappedRunStart(echoRow));
        }

        _promptStartIsSettled = false;
        _start = buffer.MarkAbsoluteLine(absoluteLine);
        _startCommandLine = commandLine;
    }

    /// <summary>D. Returns true when there is now output waiting to be cut (a C was seen).</summary>
    public bool OnCommandDone(int? exitCode)
    {
        if (_start is not { } start)
        {
            return false;
        }

        _pending = new Pending(start, _startCommandLine, exitCode);
        _start = null;
        _startCommandLine = null;
        return true;
    }

    public void Reset()
    {
        _start = null;
        _startCommandLine = null;
        _pending = null;
        _promptStart = null;
        _promptStartIsSettled = false;
    }

    /// <summary>The row in [<paramref name="promptLine"/>, <paramref name="commandLineRow"/>] where the
    /// echoed command ends. C may land on that row or, when it arrives after the echo's newline, on the
    /// next one, so the row is found by its text rather than taken from C.</summary>
    private static int? FindEchoRow(AnsiTerminalBuffer buffer, int promptLine, int commandLineRow, string? commandLine)
    {
        if (LastLine(commandLine) is not { } tail)
        {
            return null;
        }

        string[] rows = buffer.GetPlainTextForAbsoluteLineRange(promptLine, commandLineRow + 1)
            .Split(Environment.NewLine);
        string joined = string.Empty;
        for (int index = 0; index < rows.Length; index++)
        {
            joined += rows[index];
            if (ContainsEcho(joined, tail, blankBefore: false))
            {
                return promptLine + index;
            }
        }

        return null;
    }

    /// <summary>Output has gone quiet after D, so the cursor sits on the new prompt's last line: the
    /// range ends where that prompt starts. Never above the new prompt's A, which can only be early.</summary>
    public int ResolveSettledEnd(AnsiTerminalBuffer buffer)
    {
        int end = buffer.WalkBackLogicalLines(
            buffer.WrappedRunStart(buffer.CursorAbsoluteLine),
            PromptExtraLines);
        if (_pending is { } pending
            && _promptStart is { } promptStart
            && buffer.TryResolveMark(promptStart, out int promptLine)
            && buffer.TryResolveMark(pending.Start, out int startLine)
            && promptLine > startLine)
        {
            end = Math.Max(end, promptLine);
        }

        return end;
    }

    /// <summary>
    /// Cuts the pending output. <paramref name="endExclusive"/> is the row the range stops before (the
    /// new prompt's row, or the next command's C row); <paramref name="nextCommandLine"/> is the next
    /// command when cutting at its C, so its echo can be trimmed. Null when nothing was pending or the
    /// buffer was renumbered (resize reflow, scrollback clear) since the start was marked.
    /// </summary>
    public ShellCommandOutputEventArgs? Complete(
        AnsiTerminalBuffer buffer, int endExclusive, string? nextCommandLine = null)
    {
        if (_pending is not { } pending)
        {
            return null;
        }

        _pending = null;
        if (!buffer.TryGetPlainLinesFromMark(pending.Start, endExclusive, out List<string> lines, out bool headLost))
        {
            return null;
        }

        return new ShellCommandOutputEventArgs(
            pending.CommandLine,
            pending.ExitCode,
            ExtractOutput(lines, pending.CommandLine, nextCommandLine),
            headLost,
            pending.Start);
    }

    /// <summary>Drops the echoed command at the head, the next command's echo at the tail, and blank
    /// rows at both ends.</summary>
    internal static string ExtractOutput(List<string> lines, string? commandLine, string? nextCommandLine)
    {
        int first = 0;
        int last = lines.Count;
        // ConPTY sometimes repaints the echoed line a second time (at the prompt's column, on a fresh
        // row), so an echo can repeat once more after blank rows.
        for (int pass = 0; pass < 2; pass++)
        {
            first = SkipBlankHead(lines, first, last);
            // The repaint sits at the prompt's column with nothing before it.
            int rows = EchoRowsAtHead(lines, first, last, commandLine, blankBefore: pass > 0);
            if (rows == 0)
            {
                break;
            }

            first += rows;
        }

        for (int pass = 0; pass < 2; pass++)
        {
            last = SkipBlankTail(lines, first, last);
            int rows = EchoRowsAtTail(lines, first, last, nextCommandLine);
            if (rows == 0)
            {
                break;
            }

            last -= rows;
        }

        first = SkipBlankHead(lines, first, last);
        last = SkipBlankTail(lines, first, last);
        return string.Join(Environment.NewLine, lines.Skip(first).Take(last - first).Select(l => l.TrimEnd()));
    }

    private static int SkipBlankHead(List<string> lines, int first, int last)
    {
        while (first < last && string.IsNullOrWhiteSpace(lines[first]))
        {
            first++;
        }

        return first;
    }

    private static int SkipBlankTail(List<string> lines, int first, int last)
    {
        while (last > first && string.IsNullOrWhiteSpace(lines[last - 1]))
        {
            last--;
        }

        return last;
    }

    /// <summary>How many rows from <paramref name="first"/> are the echoed command line: the shortest run
    /// whose joined text contains the command's last line as an echo (ConPTY can leave stale cells after
    /// it, so not "ends with"; but `ls` must not match the "Tools" of an output row). Zero when the echo
    /// is not there (C landed after it).</summary>
    private static int EchoRowsAtHead(List<string> lines, int first, int last, string? commandLine, bool blankBefore)
    {
        if (LastLine(commandLine) is not { } tail)
        {
            return 0;
        }

        string joined = string.Empty;
        for (int count = 1; count <= Math.Min(MaxEchoRows, last - first); count++)
        {
            joined += lines[first + count - 1];
            if (ContainsEcho(joined, tail, blankBefore))
            {
                return count;
            }
        }

        return 0;
    }

    /// <summary>How many rows before <paramref name="last"/> (never reaching back past <paramref name="first"/>)
    /// are the next prompt and the next command's echo.</summary>
    private static int EchoRowsAtTail(List<string> lines, int first, int last, string? nextCommandLine)
    {
        if (LastLine(nextCommandLine) is not { } tail)
        {
            return 0;
        }

        int limit = Math.Min(MaxEchoRows, last - first);
        for (int count = 1; count <= limit; count++)
        {
            int index = last - count;
            string joined = string.Concat(lines.Skip(index).Take(count));
            if (ContainsEcho(joined, tail, blankBefore: false))
            {
                return count;
            }
        }

        return 0;
    }

    private const string PromptEndSymbols = ">$#%❯➜λ»";

    /// <summary>Whether <paramref name="text"/> holds <paramref name="command"/> the way an echo does:
    /// preceded by the row start, whitespace or a prompt's closing symbol, and followed by the row end
    /// or whitespace. With <paramref name="blankBefore"/>, only whitespace may precede it.</summary>
    internal static bool ContainsEcho(string text, string command, bool blankBefore)
    {
        for (int index = text.IndexOf(command, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(command, index + 1, StringComparison.Ordinal))
        {
            bool startOk = blankBefore
                ? string.IsNullOrWhiteSpace(text[..index])
                : index == 0 || char.IsWhiteSpace(text[index - 1]) || PromptEndSymbols.Contains(text[index - 1]);
            int end = index + command.Length;
            if (startOk && (end == text.Length || char.IsWhiteSpace(text[end])))
            {
                return true;
            }
        }

        return false;
    }

    private static string? LastLine(string? commandLine)
    {
        string? last = commandLine?
            .Split('\n')
            .Select(l => l.Trim())
            .LastOrDefault(l => l.Length > 0);
        return string.IsNullOrEmpty(last) ? null : last;
    }
}
