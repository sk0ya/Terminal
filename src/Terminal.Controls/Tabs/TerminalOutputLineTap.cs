using Terminal.Buffer;

namespace Terminal.Tabs;

/// <summary>
/// One subscriber of <see cref="TerminalTabView.SubscribeOutputLines"/>: hands out each logical line of the
/// terminal once it is finished (the cursor has moved past it), and reports the end of the first command that
/// starts after the subscription. Kept free of WPF so it can be driven straight from a buffer in tests.
/// <para>A line is "finished" when it lies before the soft-wrapped run the cursor is on — the run under the
/// cursor may still grow or be rewritten (progress bars, a prompt being typed). Lines are tracked with a
/// <see cref="TerminalLineMark"/>, so a long-running command whose output overflows the scrollback limit keeps
/// streaming; if the numbering is rebuilt (reflow, scrollback clear) the tap skips to the cursor instead of
/// replaying text it has already handed out.</para>
/// </summary>
internal sealed class TerminalOutputLineTap(Action<string> lineReceived, Action<int?>? commandFinished)
{
    private TerminalLineMark _next;
    private bool _commandStarted;
    private bool _donePending;
    private int? _exitCode;
    private TerminalLineMark? _promptStart;

    public bool IsEnded { get; private set; }

    /// <summary>True between the command's D and the moment its trailing output has settled.</summary>
    public bool IsSettling => _donePending && !IsEnded;

    public void Start(AnsiTerminalBuffer buffer)
    {
        _next = buffer.MarkAbsoluteLine(buffer.WrappedRunStart(buffer.CursorAbsoluteLine));
    }

    /// <summary>The numbering was just rebuilt (reflow, scrollback clear): carry on from the cursor. The host
    /// pumps right before such a change, so nothing finished is skipped.</summary>
    public void Resync(AnsiTerminalBuffer buffer)
    {
        if (!IsEnded)
        {
            Start(buffer);
        }
    }

    /// <summary>Hands out the lines finished since the last call.</summary>
    public void Pump(AnsiTerminalBuffer buffer)
    {
        EmitUpTo(buffer, buffer.WrappedRunStart(buffer.CursorAbsoluteLine));
    }

    public void OnShellZone(AnsiTerminalBuffer buffer, ShellCommandZoneEventArgs e)
    {
        if (IsEnded)
        {
            return;
        }

        switch (e.ZoneType)
        {
            case ShellCommandZoneType.CommandExecuted when !_donePending:
                _commandStarted = true;
                break;
            case ShellCommandZoneType.CommandDone when _commandStarted && !_donePending:
                // ConPTY forwards the marker before it paints the last of the output, so the host waits for
                // the output to go quiet (Settle) before reading up to the prompt.
                _donePending = true;
                _exitCode = e.ExitCode;
                break;
            case ShellCommandZoneType.PromptStart when _donePending:
                _promptStart = buffer.MarkAbsoluteLine(e.AbsoluteLine);
                break;
        }
    }

    /// <summary>Output went quiet after D: hand out the rest up to the new prompt and report the end.</summary>
    public void Settle(AnsiTerminalBuffer buffer)
    {
        if (!IsSettling)
        {
            return;
        }

        int end = buffer.WrappedRunStart(buffer.CursorAbsoluteLine);
        // A multi-line prompt has hard lines above the cursor's row; they are prompt, not output.
        if (_promptStart is { } prompt && buffer.TryResolveMark(prompt, out int promptLine))
        {
            end = Math.Min(end, promptLine);
        }

        EmitUpTo(buffer, end);
        Finish(_exitCode);
    }

    /// <summary>The session went away: there is no more output and no exit code to report.</summary>
    public void Abort()
    {
        if (!IsEnded)
        {
            Finish(null);
        }
    }

    public void End() => IsEnded = true;

    private void Finish(int? exitCode)
    {
        IsEnded = true;
        commandFinished?.Invoke(exitCode);
    }

    private void EmitUpTo(AnsiTerminalBuffer buffer, int endExclusive)
    {
        if (IsEnded)
        {
            return;
        }

        int start;
        if (_next.Generation != buffer.NumberingGeneration)
        {
            start = endExclusive;   // rebuilt without a Resync (e.g. ESC[3J mid-output): don't replay old text
        }
        else if (!buffer.TryResolveMark(_next, out start))
        {
            start = 0;   // the next line was evicted before we read it: carry on from the oldest kept one
        }

        if (endExclusive > start)
        {
            foreach (string line in buffer.GetLogicalLines(start, endExclusive))
            {
                if (IsEnded)
                {
                    return;   // the subscriber disposed from inside the callback
                }

                lineReceived(line);
            }
        }

        _next = buffer.MarkAbsoluteLine(Math.Max(start, endExclusive));
    }
}
