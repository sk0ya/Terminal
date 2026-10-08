namespace Terminal.Buffer;

/// <summary>
/// The terminal emulator without a view: it consumes a session's output and keeps the screen,
/// the scrollback and the modes, so that a process holding a pty for longer than any view shows it
/// (a tmux-like host) can hand a newly attached view the current state through
/// <see cref="CreateVtSnapshot"/>.
/// <para>Not thread-safe, and the emulator uses WPF value types: drive one instance from a single thread.</para>
/// </summary>
public sealed class HeadlessTerminal
{
    // Plenty for the prompts that fit in a full scrollback; the oldest go first.
    private const int MaxMarks = 20000;

    private readonly AnsiTerminalBuffer _buffer;
    private readonly List<TerminalShellMark> _marks = [];
    private long _marksRemovedHead;
    private string? _pendingCommandLine;
    private string? _runningCommandLine;
    // Set while the primary screen's rewrap is put off until the alternate screen is left.
    private List<(TerminalShellMark Mark, int Logical)>? _deferredReflowMarks;
    private long _deferredReflowRemovedBefore;

    public HeadlessTerminal(short columns, short rows, int scrollbackLimit = 10000)
    {
        _buffer = new AnsiTerminalBuffer(columns, rows, scrollbackLimit);
        _marksRemovedHead = _buffer.RemovedHeadLineCount;
        _buffer.InputSequenceGenerated += (_, text) => ResponseGenerated?.Invoke(this, text);
        _buffer.CurrentDirectoryChanged += (_, path) => CurrentDirectory = path;
        _buffer.ShellHistoryPathReceived += (_, path) => HistoryPath = path;
        _buffer.ShellCommandLineReceived += (_, command) => _pendingCommandLine = command;
        _buffer.ShellCommandZoneReceived += OnShellCommandZone;
        _buffer.AlternateScreenExited += OnAlternateScreenExited;
    }

    /// <summary>
    /// Replies the emulator owes the program (cursor position reports, device attributes, …). Only
    /// one party may answer a query, so a host forwards these only while no view is attached.
    /// </summary>
    public event EventHandler<string>? ResponseGenerated;

    public int Columns => _buffer.Columns;
    public int Rows => _buffer.Rows;
    public string WindowTitle => _buffer.WindowTitle;
    public string? CurrentDirectory { get; private set; }
    public string? HistoryPath { get; private set; }

    public void Process(string text)
    {
        _buffer.Process(text);
    }

    public void Resize(short columns, short rows)
    {
        if (columns == _buffer.Columns && rows == _buffer.Rows)
        {
            return;
        }

        SyncMarks();
        if (_marks.Count == 0 && _deferredReflowMarks is null)
        {
            _buffer.Resize(columns, rows);
            _marksRemovedHead = _buffer.RemovedHeadLineCount;
            return;
        }

        if (_buffer.IsAlternateScreenActive)
        {
            // The primary screen is not rewrapped now but when the alternate screen is left (to the
            // size of that moment), so the marks are carried across then — from the logical lines
            // as they were before the first resize, since nothing moves the primary in between.
            if (_deferredReflowMarks is null)
            {
                _deferredReflowMarks = ToLogicalMarks();
                _deferredReflowRemovedBefore = _buffer.RemovedHeadLineCount;
            }

            _buffer.Resize(columns, rows);
            _marksRemovedHead = _buffer.RemovedHeadLineCount;
            return;
        }

        List<(TerminalShellMark Mark, int Logical)> logicalMarks = ToLogicalMarks();
        long removedBefore = _buffer.RemovedHeadLineCount;
        _buffer.Resize(columns, rows);
        ApplyLogicalMarks(logicalMarks, removedBefore);
    }

    /// <summary>The whole state as a VT stream, bracketed so the reader knows it is a redraw.</summary>
    public string CreateVtSnapshot()
    {
        SyncMarks();
        return _buffer.CreateVtSnapshot(_marks, CurrentDirectory, HistoryPath, _runningCommandLine);
    }

    /// <summary>Plain text of the scrollback and screen (for tests and diagnostics).</summary>
    public string CreatePlainTextSnapshot() => _buffer.CreatePlainTextSnapshot();

    private void OnShellCommandZone(object? sender, ShellCommandZoneEventArgs e)
    {
        SyncMarks();
        switch (e.ZoneType)
        {
            case ShellCommandZoneType.CommandExecuted:
                _runningCommandLine = _pendingCommandLine;
                break;
            case ShellCommandZoneType.CommandDone:
            case ShellCommandZoneType.PromptStart:
                _runningCommandLine = null;
                _pendingCommandLine = null;
                break;
        }

        _marks.Add(new TerminalShellMark(e.ZoneType, e.AbsoluteLine, e.ExitCode));
        if (_marks.Count > MaxMarks)
        {
            _marks.RemoveRange(0, _marks.Count - MaxMarks);
        }
    }

    /// <summary>Shifts the marks up by the rows that left the head of the scrollback since last time.</summary>
    private void SyncMarks()
    {
        long removed = _buffer.RemovedHeadLineCount;
        int delta = (int)(removed - _marksRemovedHead);
        _marksRemovedHead = removed;
        if (delta == 0)
        {
            return;
        }

        for (int index = _marks.Count - 1; index >= 0; index--)
        {
            int line = _marks[index].AbsoluteLine - delta;
            if (line < 0)
            {
                _marks.RemoveAt(index);
            }
            else
            {
                _marks[index] = _marks[index] with { AbsoluteLine = line };
            }
        }
    }

    private void OnAlternateScreenExited()
    {
        if (_deferredReflowMarks is not { } logicalMarks)
        {
            return;
        }

        // Marks a program left on the alternate screen pointed into it and are gone with it.
        _deferredReflowMarks = null;
        ApplyLogicalMarks(logicalMarks, _deferredReflowRemovedBefore);
    }

    /// <summary>
    /// A width change rewraps the lines, so a mark's row number no longer names its line. Logical
    /// lines (rows joined by soft wraps) survive a rewrap, so each mark is carried across as one.
    /// </summary>
    private List<(TerminalShellMark Mark, int Logical)> ToLogicalMarks()
    {
        IReadOnlyList<TerminalLine> lines = _buffer.PrimaryLines;
        var logicalOfRow = new int[lines.Count];
        int logical = 0;
        for (int row = 0; row < lines.Count; row++)
        {
            logicalOfRow[row] = logical;
            if (!lines[row].IsWrapped)
            {
                logical++;
            }
        }

        var logicalMarks = new List<(TerminalShellMark Mark, int Logical)>(_marks.Count);
        foreach (TerminalShellMark mark in _marks)
        {
            if (mark.AbsoluteLine < logicalOfRow.Length)
            {
                logicalMarks.Add((mark, logicalOfRow[mark.AbsoluteLine]));
            }
        }

        return logicalMarks;
    }

    private void ApplyLogicalMarks(List<(TerminalShellMark Mark, int Logical)> logicalMarks, long removedBefore)
    {
        // Rows evicted from the head while rewrapping: counted as logical lines, which is exact for
        // the usual unwrapped scrollback and close enough otherwise.
        int evicted = (int)(_buffer.RemovedHeadLineCount - removedBefore);
        _marksRemovedHead = _buffer.RemovedHeadLineCount;

        IReadOnlyList<TerminalLine> lines = _buffer.PrimaryLines;
        var firstRowOfLogical = new List<int>();
        bool startsLogical = true;
        for (int row = 0; row < lines.Count; row++)
        {
            if (startsLogical)
            {
                firstRowOfLogical.Add(row);
            }

            startsLogical = !lines[row].IsWrapped;
        }

        _marks.Clear();
        foreach ((TerminalShellMark mark, int logical) in logicalMarks)
        {
            int shifted = logical - evicted;
            if (shifted >= 0 && shifted < firstRowOfLogical.Count)
            {
                _marks.Add(mark with { AbsoluteLine = firstRowOfLogical[shifted] });
            }
        }
    }
}
