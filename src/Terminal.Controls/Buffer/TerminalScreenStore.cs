namespace Terminal.Buffer;

internal readonly record struct TerminalScreenMutation(bool ScrollbackChanged);

internal sealed class TerminalScreenStore
{
    private readonly int _scrollbackLimit;

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
        return clone;
    }
}
