using System.Globalization;
using System.Text;
using System.Windows.Media;

namespace Terminal.Buffer;

/// <summary>An OSC 133 mark remembered against the absolute line it arrived on.</summary>
internal readonly record struct TerminalShellMark(ShellCommandZoneType Type, int AbsoluteLine, int? ExitCode);

/// <summary>
/// Writes the buffer's current state back out as a VT stream: fed into a fresh buffer of the same
/// size, it rebuilds the scrollback, the screen, the cursor and the modes. This is how a session that
/// outlived its view (a host process kept the pty and an emulator of its own) is shown again —
/// the same idea as tmux redrawing a pane on attach. VT rather than a private format so the reader
/// and the writer do not have to be the same version of this library.
/// </summary>
internal sealed partial class AnsiTerminalBuffer
{
    /// <summary>
    /// Private OSC that brackets a replayed snapshot. Marks and command lines that arrive inside it
    /// are history being redrawn, not things happening now, so the view records them as positions
    /// but does not report them as fresh activity.
    /// </summary>
    internal const string ReplayOscCommand = "7770";
    internal const string ReplayBeginSequence = "\u001b]7770;begin\u0007";
    internal const string ReplayEndSequence = "\u001b]7770;end\u0007";

    private static readonly Dictionary<Color, int> XtermCubeIndices = BuildXtermCubeIndices();

    /// <summary>True while a snapshot written by <see cref="CreateVtSnapshot"/> is being read back.</summary>
    public bool IsReplaying { get; private set; }

    // A snapshot is the first thing a re-attached session sends, so the bracket is honored only there.
    // Anywhere later it is just bytes a program printed (a file being cat'ed), and obeying it would
    // silence the tab's command reporting until an end that never comes.
    private bool _replayBracketAllowed = true;

    /// <summary>Raised after the alternate screen is left, once the primary screen is back (and
    /// rewrapped, if the size changed while it was away).</summary>
    internal event Action? AlternateScreenExited;

    /// <summary>The primary screen's lines, scrollback first — also while the alternate screen is up,
    /// when the primary screen is the one put aside. Absolute line numbers count over these.</summary>
    internal IReadOnlyList<TerminalLine> PrimaryLines =>
        _scrollback.Concat(_screenStore.PrimaryScreenBackup ?? _screen).ToList();

    internal int Columns => _columns;
    internal int Rows => _rows;

    /// <summary>Scrollback then screen, top to bottom (tests compare buffers through this).</summary>
    internal IEnumerable<TerminalLine> AllLinesForTests => _scrollback.Concat(_screen);

    private void DispatchReplayOsc(string value)
    {
        if (value == "begin")
        {
            IsReplaying = _replayBracketAllowed;
        }
        else if (value == "end")
        {
            IsReplaying = false;
        }

        _replayBracketAllowed = false;
    }

    /// <summary>Called at the end of each <see cref="Process"/>: past the first output, a bracket is no
    /// longer a snapshot's.</summary>
    private void CloseReplayWindow(string text)
    {
        if (text.Length > 0)
        {
            _replayBracketAllowed = false;
        }
    }

    private void RaiseAlternateScreenExited() => AlternateScreenExited?.Invoke();

    internal string CreateVtSnapshot(
        IReadOnlyList<TerminalShellMark> marks,
        string? currentDirectory,
        string? historyPath,
        string? runningCommandLine)
    {
        var writer = new SnapshotWriter(_ansiPalette);
        StringBuilder output = writer.Output;
        output.Append(ReplayBeginSequence);

        bool realAlternateScreen = _primaryScreenBackup is not null && !_syntheticAlternateScreenActive;
        IReadOnlyList<TerminalLine> primaryScreen = realAlternateScreen
            ? _screenStore.PrimaryScreenBackup ?? _screen
            : _screen;

        ILookup<int, TerminalShellMark> marksByLine = marks.ToLookup(mark => mark.AbsoluteLine);
        // The command still running is the last C with nothing after it; its command line goes
        // just before that C, the order a live shell sends them in.
        TerminalShellMark? runningMark = !string.IsNullOrEmpty(runningCommandLine) && marks.Count > 0 &&
            marks[^1].Type == ShellCommandZoneType.CommandExecuted
            ? marks[^1]
            : null;
        int scrollbackCount = _scrollback.Count;
        int total = scrollbackCount + primaryScreen.Count;
        bool previousWrapped = false;
        for (int index = 0; index < total; index++)
        {
            foreach (TerminalShellMark mark in marksByLine[index])
            {
                if (runningMark == mark)
                {
                    output.Append("\u001b]633;E;").Append(EncodeShellEscapes(runningCommandLine!)).Append('\u0007');
                }

                AppendShellMark(output, mark);
            }

            TerminalLine line = index < scrollbackCount ? _scrollback[index] : primaryScreen[index - scrollbackCount];
            writer.WriteLine(line, previousWrapped, isLast: index == total - 1);
            previousWrapped = line.IsWrapped;
        }

        if (realAlternateScreen)
        {
            ScreenState primary = _primaryScreenBackup!;
            writer.MoveCursor(primary.CursorRow, primary.CursorColumn);
            writer.SetStyle(primary.Style, primary.CurrentHyperlink);
            // 1049 saves the cursor on the way in and restores it on the way out, so leaving the
            // alternate screen later puts the cursor back on the primary prompt.
            output.Append("\u001b[?1049h");
            writer.ResetPen();
            for (int row = 0; row < _screen.Count; row++)
            {
                TerminalLine line = _screen[row];
                writer.WriteLine(line, row > 0 && _screen[row - 1].IsWrapped, isLast: row == _screen.Count - 1);
            }
        }

        AppendCursorAndRegions(writer);
        AppendModes(output);
        writer.SetStyle(_currentStyle, _currentHyperlink);

        if (!string.IsNullOrEmpty(currentDirectory))
        {
            if (Uri.TryCreate(currentDirectory, UriKind.Absolute, out Uri? directory) && directory.IsFile)
            {
                output.Append("\u001b]7;").Append(directory.AbsoluteUri).Append('\u0007');
            }
        }

        if (!string.IsNullOrEmpty(historyPath))
        {
            output.Append("\u001b]633;P;HistoryPath=").Append(EncodeShellEscapes(historyPath)).Append('\u0007');
        }

        // Last: a title change is what ends a synthetic alternate screen, so it must not land before
        // the content it would otherwise act on.
        if (!string.IsNullOrEmpty(_windowTitle))
        {
            output.Append("\u001b]2;").Append(_windowTitle).Append('\u0007');
        }

        if (!string.IsNullOrEmpty(_iconTitle) && _iconTitle != _windowTitle)
        {
            output.Append("\u001b]1;").Append(_iconTitle).Append('\u0007');
        }

        output.Append(ReplayEndSequence);
        return output.ToString();
    }

    private void AppendCursorAndRegions(SnapshotWriter writer)
    {
        StringBuilder output = writer.Output;
        // DECSTBM / DECSLRM / DECOM all home the cursor, so they go before the cursor is placed.
        if (_scrollTop != 0 || _scrollBottom != _rows - 1)
        {
            output.Append(CultureInfo.InvariantCulture, $"\u001b[{_scrollTop + 1};{_scrollBottom + 1}r");
        }

        if (_leftRightMarginEnabled)
        {
            output.Append("\u001b[?69h");
            output.Append(CultureInfo.InvariantCulture, $"\u001b[{_leftMargin + 1};{_rightMargin + 1}s");
        }

        if (_savedCursorRow != 0 || _savedCursorColumn != 0 ||
            _savedStyle != TerminalStyle.Default || _savedHyperlink is not null)
        {
            // DECSC saves the pen with the position, so a program's DECRC gets its colors back too.
            writer.MoveCursor(_savedCursorRow, _savedCursorColumn);
            writer.SetStyle(_savedStyle, _savedHyperlink);
            output.Append("\u001b7");
        }

        if (_originMode)
        {
            output.Append("\u001b[?6h");
            writer.MoveCursor(_cursorRow - _scrollTop, _cursorColumn);
        }
        else
        {
            writer.MoveCursor(_cursorRow, _cursorColumn);
        }
    }

    private void AppendModes(StringBuilder output)
    {
        void Private(int mode, bool enabled)
        {
            output.Append(CultureInfo.InvariantCulture, $"\u001b[?{mode}{(enabled ? 'h' : 'l')}");
        }

        if (_applicationCursorKeys) Private(1, true);
        if (_applicationKeypad) output.Append("\u001b=");
        if (_screenReverse) Private(5, true);
        if (!_autoWrapEnabled) Private(7, false);
        if (_reverseWraparound) Private(45, true);
        if (!_cursorVisible) Private(25, false);
        int cursorStyle = GetCursorStyleParameter();
        if (cursorStyle != 1)
        {
            output.Append(CultureInfo.InvariantCulture, $"\u001b[{cursorStyle} q");
        }

        switch (_mouseTrackingMode)
        {
            case TerminalMouseTrackingMode.X10: Private(9, true); break;
            case TerminalMouseTrackingMode.Normal: Private(1000, true); break;
            case TerminalMouseTrackingMode.ButtonEvent: Private(1002, true); break;
            case TerminalMouseTrackingMode.AnyEvent: Private(1003, true); break;
        }

        if (_useUtf8MouseEncoding) Private(1005, true);
        if (_useSgrMouseEncoding) Private(1006, true);
        if (_useUrxvtMouseEncoding) Private(1015, true);
        if (_mousePixelMode) Private(1016, true);
        if (_focusReportingEnabled) Private(1004, true);
        if (_alternateScrollEnabled) Private(1007, true);
        if (_bracketedPasteEnabled) Private(2004, true);
        if (!_altSendsEscape) Private(1036, false);
        if (_insertMode) output.Append("\u001b[4h");
        if (_lineFeedNewlineMode) output.Append("\u001b[20h");
        if (_modifyOtherKeys > 0)
        {
            output.Append(CultureInfo.InvariantCulture, $"\u001b[>4;{_modifyOtherKeys}m");
        }

        if (_kittyKeyboardFlags > 0)
        {
            output.Append(CultureInfo.InvariantCulture, $"\u001b[>{_kittyKeyboardFlags}u");
        }

        if (_g0CharacterSet == TerminalCharacterSet.DecSpecialGraphics) output.Append("\u001b(0");
        if (_g1CharacterSet == TerminalCharacterSet.DecSpecialGraphics) output.Append("\u001b)0");
        if (_glLevel == 1) output.Append('\u000e');
    }

    private static void AppendShellMark(StringBuilder output, TerminalShellMark mark)
    {
        output.Append("\u001b]133;");
        output.Append(mark.Type switch
        {
            ShellCommandZoneType.PromptStart => "A",
            ShellCommandZoneType.CommandStart => "B",
            ShellCommandZoneType.CommandExecuted => "C",
            _ => "D",
        });
        if (mark.Type == ShellCommandZoneType.CommandDone && mark.ExitCode is { } exitCode)
        {
            output.Append(';').Append(exitCode.ToString(CultureInfo.InvariantCulture));
        }

        output.Append('\u0007');
    }

    /// <summary>The inverse of <see cref="OscDecoder.DecodeShellEscapes"/>: backslash, separators
    /// and control characters go out as <c>\xHH</c>.</summary>
    internal static string EncodeShellEscapes(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char current in value)
        {
            if (current == '\\')
            {
                builder.Append("\\\\");
            }
            else if (current == ';' || current < 0x20 || current == 0x7F)
            {
                builder.Append(CultureInfo.InvariantCulture, $"\\x{(int)current:x2}");
            }
            else
            {
                builder.Append(current);
            }
        }

        return builder.ToString();
    }

    private static Dictionary<Color, int> BuildXtermCubeIndices()
    {
        var indices = new Dictionary<Color, int>();
        for (int index = 16; index <= 255; index++)
        {
            indices.TryAdd(SgrInterpreter.ResolveXtermColor(index, [], default), index);
        }

        return indices;
    }

    private sealed class SnapshotWriter(Color[] palette)
    {
        private TerminalStyle _style = TerminalStyle.Default;
        private TerminalHyperlink? _hyperlink;

        public StringBuilder Output { get; } = new();

        public void ResetPen()
        {
            _style = TerminalStyle.Default;
            _hyperlink = null;
        }

        public void MoveCursor(int row, int column)
        {
            Output.Append(CultureInfo.InvariantCulture, $"\u001b[{row + 1};{column + 1}H");
        }

        public void WriteLine(TerminalLine line, bool previousWrapped, bool isLast)
        {
            TerminalCell[] cells = line.Cells;
            int end = line.IsWrapped ? cells.Length - 1 : LastMeaningfulColumn(cells);
            if (end < 0 && previousWrapped)
            {
                // Something has to land on this row for the row above to count as wrapped into it.
                SetStyle(TerminalStyle.Default, null);
                Output.Append(' ');
            }

            for (int column = 0; column <= end; column++)
            {
                TerminalCell cell = cells[column];
                if (cell.IsContinuation)
                {
                    continue;
                }

                SetStyle(cell.Style, cell.Hyperlink);
                Output.Append(cell.Text);
            }

            if (isLast)
            {
                return;
            }

            if (!line.IsWrapped)
            {
                // Back to the default pen before the line feed, so a scroll it causes fills with nothing.
                SetStyle(TerminalStyle.Default, null);
                Output.Append("\r\n");
            }
        }

        public void SetStyle(TerminalStyle style, TerminalHyperlink? hyperlink)
        {
            if (!ReferenceEquals(hyperlink, _hyperlink))
            {
                Output.Append("\u001b]8;");
                if (hyperlink is not null)
                {
                    if (hyperlink.Id is not null)
                    {
                        Output.Append("id=").Append(hyperlink.Id);
                    }

                    Output.Append(';').Append(hyperlink.Uri);
                }
                else
                {
                    Output.Append(';');
                }

                Output.Append('\u0007');
                _hyperlink = hyperlink;
            }

            if (style == _style)
            {
                return;
            }

            Output.Append("\u001b[0");
            if (style.Bold) Output.Append(";1");
            if (style.Dim) Output.Append(";2");
            if (style.Italic) Output.Append(";3");
            switch (style.UnderlineStyle)
            {
                case UnderlineStyle.Single: Output.Append(";4"); break;
                case UnderlineStyle.Double: Output.Append(";4:2"); break;
                case UnderlineStyle.Curly: Output.Append(";4:3"); break;
                case UnderlineStyle.Dotted: Output.Append(";4:4"); break;
                case UnderlineStyle.Dashed: Output.Append(";4:5"); break;
            }

            if (style.Blink) Output.Append(";5");
            if (style.Inverse) Output.Append(";7");
            if (style.Invisible) Output.Append(";8");
            if (style.Strikethrough) Output.Append(";9");
            if (style.Overline) Output.Append(";53");
            AppendColor(style.Foreground, 30, 90, 38);
            AppendColor(style.Background, 40, 100, 48);
            if (style.UnderlineColor is Color underline)
            {
                Output.Append(CultureInfo.InvariantCulture, $";58;2;{underline.R};{underline.G};{underline.B}");
            }

            Output.Append('m');
            _style = style;
        }

        /// <summary>
        /// Colors were resolved to RGB when they were written, against this buffer's palette. Writing
        /// them back as RGB would pin them to that palette, so a color that is a palette entry goes
        /// back out as its index and the reader resolves it against its own theme.
        /// </summary>
        private void AppendColor(Color? color, int normalBase, int brightBase, int extended)
        {
            if (color is not Color value)
            {
                return;
            }

            int paletteIndex = Array.IndexOf(palette, value);
            if (paletteIndex is >= 0 and < 8)
            {
                Output.Append(';').Append((normalBase + paletteIndex).ToString(CultureInfo.InvariantCulture));
            }
            else if (paletteIndex is >= 8 and < 16)
            {
                Output.Append(';').Append((brightBase + paletteIndex - 8).ToString(CultureInfo.InvariantCulture));
            }
            else if (XtermCubeIndices.TryGetValue(value, out int cubeIndex))
            {
                Output.Append(CultureInfo.InvariantCulture, $";{extended};5;{cubeIndex}");
            }
            else
            {
                Output.Append(CultureInfo.InvariantCulture, $";{extended};2;{value.R};{value.G};{value.B}");
            }
        }

        private static int LastMeaningfulColumn(TerminalCell[] cells)
        {
            for (int column = cells.Length - 1; column >= 0; column--)
            {
                TerminalCell cell = cells[column];
                if (cell.IsContinuation || cell.Text != " " || cell.Style != TerminalStyle.Default || cell.Hyperlink is not null)
                {
                    return column;
                }
            }

            return -1;
        }
    }
}
