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

    public void ReplaceScreen(List<TerminalLine> screen)
    {
        Screen = screen;
    }

    public void ApplyReflow(List<TerminalLine> screen, IEnumerable<TerminalLine> scrollback)
    {
        Screen = screen;
        Scrollback.Clear();
        Scrollback.AddRange(scrollback);
    }

    public void ClearScrollback()
    {
        Scrollback.Clear();
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
        }

        return Math.Max(overflow, 0);
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
        bool clearWrapped = false)
    {
        int start = Math.Clamp(startColumn, 0, columns);
        int end = Math.Clamp(endExclusive, 0, columns);
        for (int column = start; column < end; column++)
        {
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
        ClearWideOverlap(line, column, columns, style);
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

    private static void ClearWideOverlap(TerminalLine line, int column, int columns, TerminalStyle blankStyle)
    {
        if (column > 0 && line.Cells[column].IsContinuation)
        {
            line.Cells[column - 1] = TerminalCell.CreateBlank(blankStyle);
            line.Cells[column] = TerminalCell.CreateBlank(blankStyle);
        }

        if (column + 1 < columns &&
            line.Cells[column + 1].IsContinuation &&
            !line.Cells[column].IsContinuation)
        {
            line.Cells[column] = TerminalCell.CreateBlank(blankStyle);
            line.Cells[column + 1] = TerminalCell.CreateBlank(blankStyle);
        }
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
        clone.IsWrapped = line.IsWrapped;
        clone.LineSize = line.LineSize;
        return clone;
    }

    private static List<TerminalLine> CloneScreen(IEnumerable<TerminalLine> source)
    {
        return source.Select(CloneLine).ToList();
    }
}
