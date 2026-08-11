using Terminal.Buffer;

namespace Terminal.Tests;

public sealed class TerminalCursorWrapTests
{
    [Fact]
    public void LineFeedAfterLastColumnMovesDownWithoutAnExtraWrap()
    {
        var buffer = new AnsiTerminalBuffer(20, 10);

        buffer.Process(new string('A', 20) + "\nX");

        Assert.Equal(1, buffer.CursorRow);
        Assert.Equal('X', buffer.GetScreenLineText(1)[19]);
        Assert.DoesNotContain('X', buffer.GetScreenLineText(2));
    }

    [Fact]
    public void CursorReportAfterLastColumnUsesTheLastGridColumn()
    {
        var buffer = new AnsiTerminalBuffer(20, 10);
        string? response = null;
        buffer.InputSequenceGenerated += (_, value) => response = value;

        buffer.Process(new string('A', 20));
        buffer.Process("\u001b[6n");

        Assert.Equal("\u001b[1;20R", response);
    }

    [Fact]
    public void CombiningMarkAfterHardLineBreakDoesNotModifyThePreviousLine()
    {
        var buffer = new AnsiTerminalBuffer(20, 10);

        buffer.Process("a\r\n\u0301");

        Assert.Equal("a", buffer.GetScreenLineText(0).TrimEnd());
        Assert.DoesNotContain('\u0301', buffer.GetScreenLineText(0));
        Assert.DoesNotContain('\u0301', buffer.GetScreenLineText(1));
    }

    [Fact]
    public void RepCanRepeatWideClusterAfterADeferredWrapPosition()
    {
        var buffer = new AnsiTerminalBuffer(20, 10);

        buffer.Process(new string('A', 18) + "界");
        buffer.Process("\u001b[2b");

        Assert.Equal("界界", buffer.GetScreenLineText(1).TrimEnd());
        Assert.Equal(1, buffer.CursorRow);
        Assert.Equal(4, buffer.CursorColumn);
    }

    [Fact]
    public void GrowingWidthMovesDeferredWrapToTheNewLogicalEnd()
    {
        var buffer = new AnsiTerminalBuffer(20, 10);

        buffer.Process(new string('A', 20));
        buffer.Resize(21, 10);
        Assert.False(buffer.WrapPendingForTests);
        buffer.Process("X");

        Assert.Equal(0, buffer.CursorRow);
        Assert.Equal('X', buffer.GetScreenLineText(0)[20]);
    }

    [Fact]
    public void ShrinkingWidthKeepsDeferredWrapAfterTheReflowedLastCell()
    {
        var buffer = new AnsiTerminalBuffer(40, 10);

        buffer.Process(new string('A', 40));
        buffer.Resize(20, 10);
        Assert.Equal(1, buffer.CursorRow);
        Assert.Equal(19, buffer.CursorColumn);
        Assert.True(buffer.WrapPendingForTests);
        buffer.Process("X");

        Assert.Equal(2, buffer.CursorRow);
        Assert.Equal('X', buffer.GetScreenLineText(2)[0]);
    }
}
