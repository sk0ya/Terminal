using Terminal.Buffer;

namespace Terminal.Tests;

public sealed class TerminalReflowCalculatorTests
{
    [Fact]
    public void ReflowMovesWideCellAtRightEdgeToNextLine()
    {
        TerminalLine source = CreateLine(6, "abcd");
        source.Cells[4] = Cell("界", width: 2);
        source.Cells[5] = Cell(string.Empty, isContinuation: true, width: 0);

        List<TerminalLine> result = Reflow([source], 5, 0, 0, out _, out _);

        Assert.Equal(2, result.Count);
        Assert.True(result[0].IsWrapped);
        Assert.Equal("abcd", Text(result[0]));
        Assert.Equal("界", Text(result[1]));
        Assert.False(result[1].Cells[0].IsContinuation);
        Assert.Equal(2, result[1].Cells[0].Width);
    }

    [Fact]
    public void ReflowTreatsWrappedSourceRowsAsOneLogicalLine()
    {
        TerminalLine first = CreateLine(4, "abcd");
        first.IsWrapped = true;
        TerminalLine second = CreateLine(4, "ef");

        List<TerminalLine> result = Reflow([first, second], 3, 0, 0, out _, out _);

        Assert.Equal(2, result.Count);
        Assert.Equal("abc", Text(result[0]));
        Assert.True(result[0].IsWrapped);
        Assert.Equal("def", Text(result[1]));
        Assert.False(result[1].IsWrapped);
    }

    [Fact]
    public void ReflowMapsCursorWithinLogicalLine()
    {
        TerminalLine source = CreateLine(6, "abcdef");

        _ = Reflow([source], 4, 0, 5, out int cursorRow, out int cursorColumn);

        Assert.Equal(1, cursorRow);
        Assert.Equal(1, cursorColumn);
    }

    private static List<TerminalLine> Reflow(
        List<TerminalLine> source,
        int columns,
        int cursorRow,
        int cursorColumn,
        out int mappedRow,
        out int mappedColumn)
    {
        return TerminalReflowCalculator.ReflowLines(
            source,
            columns,
            cursorRow,
            cursorColumn,
            out mappedRow,
            out mappedColumn,
            cursorRow,
            cursorColumn,
            out _,
            out _);
    }

    private static TerminalLine CreateLine(int columns, string text)
    {
        var line = new TerminalLine(columns, TerminalStyle.Default);
        for (int index = 0; index < text.Length; index++)
        {
            line.Cells[index] = Cell(text[index].ToString());
        }

        return line;
    }

    private static TerminalCell Cell(string text, bool isContinuation = false, int width = 1) =>
        new(text, TerminalStyle.Default, Hyperlink: null, isContinuation, width);

    private static string Text(TerminalLine line) =>
        string.Concat(line.Cells.Where(cell => !cell.IsContinuation).Select(cell => cell.Text)).TrimEnd();
}
