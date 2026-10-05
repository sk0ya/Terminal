using Terminal.Buffer;

namespace Terminal.Tests;

/// <summary>
/// A line remembered at OSC 133;C must still find the command's output later, even when the
/// scrollback limit dropped lines off its head in between (absolute numbers shift by that much).
/// </summary>
public sealed class AnsiTerminalBufferLineMarkTests
{
    private const short Columns = 32;
    private const short Rows = 5;

    [Fact]
    public void MarkResolvesThroughScrollbackEviction()
    {
        var buffer = new AnsiTerminalBuffer(Columns, Rows, scrollbackLimit: 10);
        buffer.Process("before\r\n");
        var mark = buffer.MarkAbsoluteLine(buffer.CursorAbsoluteLine);
        buffer.Process(string.Join("\r\n", Enumerable.Range(1, 8).Select(i => $"out{i}")) + "\r\n");

        Assert.True(buffer.TryGetPlainLinesFromMark(mark, buffer.CursorAbsoluteLine, out var lines, out bool headLost));
        Assert.False(headLost);
        Assert.Equal(Enumerable.Range(1, 8).Select(i => $"out{i}"), lines);

        // Once the start has been pushed past the limit, the lines begin at the oldest kept one.
        var early = buffer.MarkAbsoluteLine(buffer.CursorAbsoluteLine);
        buffer.Process(string.Join("\r\n", Enumerable.Range(1, 30).Select(i => $"more{i}")) + "\r\n");
        Assert.True(buffer.TryGetPlainLinesFromMark(early, buffer.CursorAbsoluteLine, out lines, out headLost));
        Assert.True(headLost);
        Assert.DoesNotContain("more1", lines);
        Assert.Equal("more30", lines[^1]);
    }

    [Fact]
    public void MarkIsRejectedAfterReflow()
    {
        var buffer = new AnsiTerminalBuffer(Columns, Rows);
        var mark = buffer.MarkAbsoluteLine(buffer.CursorAbsoluteLine);
        buffer.Process("out\r\n");
        buffer.Resize(Columns + 8, Rows);

        Assert.False(buffer.TryGetPlainLinesFromMark(mark, buffer.CursorAbsoluteLine, out _, out _));
    }
}
