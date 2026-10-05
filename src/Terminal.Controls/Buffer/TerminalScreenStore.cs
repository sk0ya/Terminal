namespace Terminal.Buffer;

internal readonly record struct TerminalScreenMutation(bool ScrollbackChanged);

internal sealed class TerminalScreenStore
{
    private readonly int _scrollbackLimit;
    private List<TerminalLine>? _primaryScreenBackup;
    private List<TerminalLine>? _pendingPrimaryScreenBackup;

    public TerminalScreenStore(int rows, int columns, int scrollbackLimit)
    {
        _scrollbackLimit = Math.Max(0, scrollbackLimit);
        Screen = CreateScreen(rows, columns, TerminalStyle.Default);
    }

    public List<TerminalLine> Screen { get; private set; }
    public List<TerminalLine> Scrollback { get; } = [];
    public int ScrollbackLimit => _scrollbackLimit;

    /// <summary>Lines dropped off the head of the scrollback so far. Absolute line numbers shift down
    /// by this much, so a line number remembered earlier is corrected by the difference.</summary>
    public long EvictedLineCount { get; private set; }

    /// <summary>Bumped whenever the scrollback is rebuilt or cleared (reflow, clear). A line number
    /// remembered under an older generation no longer points at the same text.</summary>
    public int NumberingGeneration { get; private set; }

    public void ReplaceScreen(List<TerminalLine> screen)
    {
        Screen = screen;
    }

    public void ApplyReflow(List<TerminalLine> screen, IEnumerable<TerminalLine> scrollback)
    {
        Screen = screen;
        Scrollback.Clear();
        Scrollback.AddRange(scrollback);
        NumberingGeneration++;
    }

    public void ClearScrollback()
    {
        Scrollback.Clear();
        NumberingGeneration++;
    }

    public bool EnterAlternateScreen(int rows, int columns)
    {
        if (_primaryScreenBackup is not null)
        {
            return false;
        }

        _pendingPrimaryScreenBackup = null;
        _primaryScreenBackup = CloneScreen(Screen);
        Screen = CreateScreen(rows, columns, TerminalStyle.Default);
        return true;
    }

    public bool ExitAlternateScreen()
    {
        if (_primaryScreenBackup is null)
        {
            return false;
        }

        Screen = CloneScreen(_primaryScreenBackup);
        _primaryScreenBackup = null;
        return true;
    }

    public void CapturePendingPrimaryScreen()
    {
        if (_primaryScreenBackup is null)
        {
            _pendingPrimaryScreenBackup = CloneScreen(Screen);
        }
    }

    public void ClearPendingPrimaryScreen()
    {
        _pendingPrimaryScreenBackup = null;
    }

    public void ResetAlternateState()
    {
        _primaryScreenBackup = null;
        _pendingPrimaryScreenBackup = null;
    }

    public void PromotePendingOrCapturePrimaryScreen()
    {
        if (_primaryScreenBackup is not null)
        {
            return;
        }

        _primaryScreenBackup = _pendingPrimaryScreenBackup ?? CloneScreen(Screen);
        _pendingPrimaryScreenBackup = null;
    }

    public int AppendScrollback(TerminalLine line)
    {
        Scrollback.Add(line);
        int overflow = Scrollback.Count - _scrollbackLimit;
        if (overflow > 0)
        {
            Scrollback.RemoveRange(0, overflow);
            EvictedLineCount += overflow;
        }

        return Math.Max(overflow, 0);
    }

    /// <summary>Copies the screen's first <paramref name="rowCount"/> rows into the scrollback,
    /// and answers how many of them the scrollback kept.</summary>
    public int AppendScreenToScrollback(int rowCount)
    {
        int count = Math.Clamp(rowCount, 0, Screen.Count);
        for (int row = 0; row < count; row++)
        {
            AppendScrollback(CloneLine(Screen[row]));
        }

        return Math.Min(count, Scrollback.Count);
    }

    /// <summary>Drops the last <paramref name="count"/> scrollback rows.</summary>
    public void RemoveScrollbackTail(int count)
    {
        int removeCount = Math.Clamp(count, 0, Scrollback.Count);
        if (removeCount > 0)
        {
            Scrollback.RemoveRange(Scrollback.Count - removeCount, removeCount);
        }
    }

    public TerminalScreenMutation ScrollUp(
        int lines,
        int top,
        int bottom,
        int columns,
        TerminalStyle blankStyle,
        bool appendToScrollback)
    {
        int count = Math.Clamp(lines, 1, bottom - top + 1);
        if (appendToScrollback)
        {
            for (int row = 0; row < count; row++)
            {
                AppendScrollback(CloneLine(Screen[top + row]));
            }
        }

        for (int row = top; row <= bottom - count; row++)
        {
            Screen[row] = Screen[row + count];
        }

        for (int row = bottom - count + 1; row <= bottom; row++)
        {
            Screen[row] = new TerminalLine(columns, blankStyle);
        }

        return new TerminalScreenMutation(appendToScrollback);
    }

    public void ScrollDown(int lines, int top, int bottom, int columns, TerminalStyle blankStyle)
    {
        int count = Math.Clamp(lines, 1, bottom - top + 1);
        for (int row = bottom; row >= top + count; row--)
        {
            Screen[row] = Screen[row - count];
        }

        for (int row = top; row < top + count; row++)
        {
            Screen[row] = new TerminalLine(columns, blankStyle);
        }
    }

    public void InsertLines(int cursorRow, int scrollTop, int scrollBottom, int count, int columns, TerminalStyle blankStyle)
    {
        if (cursorRow < scrollTop || cursorRow > scrollBottom)
        {
            return;
        }

        int lineCount = Math.Min(Math.Max(count, 1), scrollBottom - cursorRow + 1);
        for (int row = scrollBottom; row >= cursorRow + lineCount; row--)
        {
            Screen[row] = Screen[row - lineCount];
        }

        for (int row = 0; row < lineCount; row++)
        {
            Screen[cursorRow + row] = new TerminalLine(columns, blankStyle);
        }
    }

    public void DeleteLines(int cursorRow, int scrollTop, int scrollBottom, int count, int columns, TerminalStyle blankStyle)
    {
        if (cursorRow < scrollTop || cursorRow > scrollBottom)
        {
            return;
        }

        int lineCount = Math.Min(Math.Max(count, 1), scrollBottom - cursorRow + 1);
        for (int row = cursorRow; row <= scrollBottom - lineCount; row++)
        {
            Screen[row] = Screen[row + lineCount];
        }

        for (int row = scrollBottom - lineCount + 1; row <= scrollBottom; row++)
        {
            Screen[row] = new TerminalLine(columns, blankStyle);
        }
    }

    public void InsertCharacters(int row, int column, int rightLimit, int count, TerminalStyle blankStyle)
    {
        int insertCount = Math.Min(Math.Max(count, 1), rightLimit - column);
        BreakWideCellsAt(Screen[row], blankStyle, column, rightLimit - insertCount, rightLimit);
        TerminalCell[] cells = Screen[row].Cells;
        for (int target = rightLimit - 1; target >= column + insertCount; target--)
        {
            cells[target] = cells[target - insertCount];
        }

        FillRange(row, column, column + insertCount, cells.Length, blankStyle);
    }

    public void DeleteCharacters(int row, int column, int rightLimit, int count, TerminalStyle blankStyle)
    {
        int deleteCount = Math.Min(Math.Max(count, 1), rightLimit - column);
        BreakWideCellsAt(Screen[row], blankStyle, column, column + deleteCount, rightLimit);
        TerminalCell[] cells = Screen[row].Cells;
        for (int target = column; target < rightLimit - deleteCount; target++)
        {
            cells[target] = cells[target + deleteCount];
        }

        FillRange(row, rightLimit - deleteCount, rightLimit, cells.Length, blankStyle);
    }

    public void ScrollLeft(
        int top,
        int bottom,
        int left,
        int rightLimit,
        int count,
        TerminalStyle blankStyle)
    {
        int scrollCount = Math.Min(Math.Max(count, 1), rightLimit - left);
        for (int row = top; row <= bottom; row++)
        {
            BreakWideCellsAt(Screen[row], blankStyle, left, left + scrollCount, rightLimit);
            TerminalCell[] cells = Screen[row].Cells;
            for (int target = left; target < rightLimit - scrollCount; target++)
            {
                cells[target] = cells[target + scrollCount];
            }

            FillRange(row, rightLimit - scrollCount, rightLimit, cells.Length, blankStyle);
        }
    }

    public void ScrollRight(
        int top,
        int bottom,
        int left,
        int rightLimit,
        int count,
        TerminalStyle blankStyle)
    {
        int scrollCount = Math.Min(Math.Max(count, 1), rightLimit - left);
        for (int row = top; row <= bottom; row++)
        {
            BreakWideCellsAt(Screen[row], blankStyle, left, rightLimit - scrollCount, rightLimit);
            TerminalCell[] cells = Screen[row].Cells;
            for (int target = rightLimit - 1; target >= left + scrollCount; target--)
            {
                cells[target] = cells[target - scrollCount];
            }

            FillRange(row, left, left + scrollCount, cells.Length, blankStyle);
        }
    }

    public void InsertColumns(
        int top,
        int bottom,
        int left,
        int rightLimit,
        int count,
        TerminalStyle blankStyle)
    {
        ScrollRight(top, bottom, left, rightLimit, count, blankStyle);
    }

    public void DeleteColumns(
        int top,
        int bottom,
        int left,
        int rightLimit,
        int count,
        TerminalStyle blankStyle)
    {
        ScrollLeft(top, bottom, left, rightLimit, count, blankStyle);
    }

    public void EraseCharacters(int row, int column, int count, int columns, TerminalStyle blankStyle)
    {
        int eraseCount = Math.Min(Math.Max(count, 1), columns - column);
        FillRange(row, column, column + eraseCount, columns, blankStyle);
    }

    public void FillRange(
        int row,
        int startColumn,
        int endExclusive,
        int columns,
        TerminalStyle blankStyle,
        bool clearWrapped = false,
        bool selective = false)
    {
        int start = Math.Clamp(startColumn, 0, columns);
        int end = Math.Clamp(endExclusive, 0, columns);
        // Selective erase leaves protected cells alone, and a protected glyph must not lose its
        // other half to it either - but an unprotected glyph on the edge is broken like any other.
        BreakWideCellsStraddling(Screen[row], start, end, blankStyle, keepProtected: selective);

        for (int column = start; column < end; column++)
        {
            if (selective && Screen[row].Cells[column].Style.Protected)
            {
                continue;
            }

            Screen[row].Cells[column] = TerminalCell.CreateBlank(blankStyle);
        }

        if (clearWrapped)
        {
            Screen[row].IsWrapped = false;
        }
    }

    public void FillAlignment()
    {
        foreach (TerminalLine line in Screen)
        {
            for (int column = 0; column < line.Cells.Length; column++)
            {
                line.Cells[column] = new TerminalCell(
                    "E",
                    TerminalStyle.Default,
                    Hyperlink: null,
                    IsContinuation: false,
                    Width: 1);
            }

            line.IsWrapped = false;
            line.Images.Clear();
        }
    }

    public void ClearImages()
    {
        foreach (TerminalLine line in Screen)
        {
            line.Images.Clear();
        }
    }

    /// <summary>Drops every image anchored in <paramref name="row"/>, ignoring rows out of range.</summary>
    public void ClearImages(int row)
    {
        if ((uint)row < (uint)Screen.Count)
        {
            Screen[row].Images.Clear();
        }
    }

    /// <summary>
    /// Drops the images anchored in <paramref name="row"/> whose column falls inside
    /// [<paramref name="fromColumn"/>, <paramref name="toColumnExclusive"/>), for a partial erase.
    /// </summary>
    public void ClearImages(int row, int fromColumn, int toColumnExclusive)
    {
        if ((uint)row >= (uint)Screen.Count)
        {
            return;
        }

        Screen[row].Images.RemoveAll(
            image => image.Column >= fromColumn && image.Column < toColumnExclusive);
    }

    public void RemoveImages(Func<TerminalImage, bool> predicate)
    {
        foreach (TerminalLine line in Screen)
        {
            line.Images.RemoveAll(image => predicate(image));
        }
    }

    public void PlaceCell(
        int row,
        int column,
        string text,
        int width,
        int columns,
        TerminalStyle style,
        string? hyperlink)
    {
        TerminalLine line = Screen[row];
        BreakWideCellsStraddling(line, column, column + width, style);
        line.Cells[column] = new TerminalCell(text, style, hyperlink, IsContinuation: false, Width: width);
        if (width == 2 && column + 1 < columns)
        {
            line.Cells[column + 1] = new TerminalCell(
                string.Empty,
                style,
                hyperlink,
                IsContinuation: true,
                Width: 0);
        }
    }

    /// <summary>
    /// Blanks every wide character that crosses either edge of the column range about to be
    /// rewritten, so the edit can never leave half of one behind.
    /// </summary>
    /// <remarks>
    /// Only the left edge used to be checked. A wide glyph written one cell to the right of another
    /// wide glyph took over the old lead cell but left its continuation cell standing; the next
    /// character written onto that orphan then "repaired" it by blanking the continuation of the
    /// glyph just written. The line ended up one cell wider than the screen and everything after it
    /// drew one column to the right - which is exactly what an app's diff renderer produces when the
    /// user inserts or deletes a character in the middle of Japanese text. An erase that started or
    /// ended mid-glyph left the same kind of half behind.
    /// </remarks>
    private static void BreakWideCellsStraddling(
        TerminalLine line,
        int startColumn,
        int endExclusive,
        TerminalStyle blankStyle,
        bool keepProtected = false)
    {
        BreakWideCellAt(line, startColumn, blankStyle, keepProtected);
        BreakWideCellAt(line, endExclusive, blankStyle, keepProtected);
    }

    /// <summary>
    /// Blanks the wide characters cut by any of <paramref name="boundaries"/> before cells are shifted
    /// sideways: the insertion/deletion point, the point past which cells fall off, and the margin.
    /// </summary>
    private static void BreakWideCellsAt(TerminalLine line, TerminalStyle blankStyle, params int[] boundaries)
    {
        foreach (int boundary in boundaries)
        {
            BreakWideCellAt(line, boundary, blankStyle, keepProtected: false);
        }
    }

    /// <summary>Blanks the wide character whose two halves sit on either side of <paramref name="boundary"/>.</summary>
    internal static void BreakWideCellAt(
        TerminalLine line,
        int boundary,
        TerminalStyle blankStyle,
        bool keepProtected = false)
    {
        TerminalCell[] cells = line.Cells;
        if (boundary <= 0 || boundary >= cells.Length || !cells[boundary].IsContinuation)
        {
            return;
        }

        // Both halves carry the glyph's style, so the continuation answers for the whole glyph.
        if (keepProtected && cells[boundary].Style.Protected)
        {
            return;
        }

        // A continuation whose lead is already gone is blanked on its own; the narrow cell to its
        // left belongs to nobody's glyph and must survive.
        if (cells[boundary - 1].Width == 2 && !cells[boundary - 1].IsContinuation)
        {
            cells[boundary - 1] = TerminalCell.CreateBlank(blankStyle);
        }

        cells[boundary] = TerminalCell.CreateBlank(blankStyle);
    }

    private static List<TerminalLine> CreateScreen(int rows, int columns, TerminalStyle blankStyle)
    {
        var screen = new List<TerminalLine>(rows);
        for (int row = 0; row < rows; row++)
        {
            screen.Add(new TerminalLine(columns, blankStyle));
        }

        return screen;
    }

    private static TerminalLine CloneLine(TerminalLine line)
    {
        var clone = new TerminalLine(line.Cells.Length, TerminalStyle.Default);
        Array.Copy(line.Cells, clone.Cells, line.Cells.Length);
        clone.Images.AddRange(line.Images);
        clone.IsWrapped = line.IsWrapped;
        clone.LineSize = line.LineSize;
        return clone;
    }

    private static List<TerminalLine> CloneScreen(IEnumerable<TerminalLine> source)
    {
        return source.Select(CloneLine).ToList();
    }
}
