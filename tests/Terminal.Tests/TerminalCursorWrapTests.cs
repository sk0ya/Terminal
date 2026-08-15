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

    /// <summary>
    /// A colour change is not a cursor movement, so it must not cancel the deferred wrap. Claude
    /// Code's input box is drawn exactly this way - a rule filling the row, then the colour of the
    /// caret, then the caret itself - and cancelling the wrap there put the caret on top of the
    /// rule's last cell instead of at the start of the input row, where it was invisible against
    /// the rule.
    /// </summary>
    [Fact]
    public void ColourChangeAfterLastColumnKeepsTheDeferredWrap()
    {
        var buffer = new AnsiTerminalBuffer(20, 10);

        buffer.Process(new string('─', 20));
        buffer.Process("[38;5;208m❯");

        Assert.Equal(new string('─', 20), buffer.GetScreenLineText(0));
        Assert.Equal(1, buffer.CursorRow);
        Assert.Equal("❯", buffer.GetScreenLineText(1).TrimEnd());
    }

    /// <summary>
    /// The same for every other sequence that only reports, sets an attribute or flips a mode: none
    /// of them touch the cursor, so none of them may drop a pending wrap. The synchronized-update
    /// and cursor-visibility modes are the ones that show up in practice - a program redrawing a
    /// full-width rule brackets the redraw with them.
    /// </summary>
    [Theory]
    [InlineData("[0m")]        // SGR reset
    [InlineData("[>4;2m")]     // modifyOtherKeys
    [InlineData("[c")]         // device attributes
    [InlineData("[6n")]        // cursor position report
    [InlineData("[2 q")]       // cursor style
    [InlineData("[5i")]        // media copy, consumed
    [InlineData("[?2026h")]    // begin synchronized update
    [InlineData("[?2026l")]    // end synchronized update
    [InlineData("[?25l")]      // hide cursor
    [InlineData("[?25h")]      // show cursor
    [InlineData("[4h")]        // insert mode
    [InlineData("[?1000h")]    // mouse tracking
    [InlineData("[?2004h")]    // bracketed paste
    public void ReportingSequenceAfterLastColumnKeepsTheDeferredWrap(string sequence)
    {
        var buffer = new AnsiTerminalBuffer(20, 10);

        buffer.Process(new string('A', 20));
        buffer.Process(sequence + "X");

        Assert.Equal(1, buffer.CursorRow);
        Assert.Equal("X", buffer.GetScreenLineText(1).TrimEnd());
    }

    /// <summary>
    /// Claude Code's input box, as the pty sends it: the redraw is bracketed by a synchronized
    /// update, the rule fills the row exactly and leaves the wrap pending, the cursor is hidden, and
    /// only then comes the caret that is supposed to perform the wrap. Dropping the wrap on any of
    /// those drew the caret over the rule's last cell, where it was invisible, and left the input
    /// row starting one cell late.
    /// </summary>
    [Fact]
    public void InputBoxCaretWrapsOntoItsOwnRowThroughASynchronizedRedraw()
    {
        var buffer = new AnsiTerminalBuffer(20, 10);

        buffer.Process("[?2026h");
        buffer.Process("[1;1H" + new string('─', 20));
        buffer.Process("[?25l[38;5;208m❯ Try");
        buffer.Process("[?25h[?2026l");

        Assert.Equal(new string('─', 20), buffer.GetScreenLineText(0));
        Assert.Equal("❯ Try", buffer.GetScreenLineText(1).TrimEnd());
        Assert.Equal(1, buffer.CursorRow);
    }

    /// <summary>Anything that does place the cursor still cancels it.</summary>
    [Theory]
    [InlineData("[1C")]        // cursor forward
    [InlineData("[K")]         // erase in line
    [InlineData("[1;20H")]     // absolute position
    [InlineData("[?6h")]       // origin mode homes the cursor
    [InlineData("[?1049h")]    // alternate screen homes the cursor
    public void CursorSequenceAfterLastColumnCancelsTheDeferredWrap(string sequence)
    {
        var buffer = new AnsiTerminalBuffer(20, 10);

        buffer.Process(new string('A', 20));
        buffer.Process(sequence + "X");

        Assert.Equal(0, buffer.CursorRow);
    }

    /// <summary>
    /// DECSC/DECRC and their CSI spellings carry the pending wrap across, so a save taken at the
    /// last column has to record that the wrap was still owed.
    /// </summary>
    [Fact]
    public void SaveAndRestoreCarryTheDeferredWrap()
    {
        var buffer = new AnsiTerminalBuffer(20, 10);

        buffer.Process(new string('A', 20));
        buffer.Process("[s");
        buffer.Process("[5;5HZ");
        buffer.Process("[u");
        buffer.Process("X");

        Assert.Equal(1, buffer.CursorRow);
        Assert.Equal("X", buffer.GetScreenLineText(1).TrimEnd());
    }

    [Fact]
    public void CursorReportAfterLastColumnUsesTheLastGridColumn()
    {
        var buffer = new AnsiTerminalBuffer(20, 10);
        string? response = null;
        buffer.InputSequenceGenerated += (_, value) => response = value;

        buffer.Process(new string('A', 20));
        buffer.Process("[6n");

        Assert.Equal("[1;20R", response);
    }

    [Fact]
    public void CombiningMarkAfterHardLineBreakDoesNotModifyThePreviousLine()
    {
        var buffer = new AnsiTerminalBuffer(20, 10);

        buffer.Process("a\r\ń");

        Assert.Equal("a", buffer.GetScreenLineText(0).TrimEnd());
        Assert.DoesNotContain('́', buffer.GetScreenLineText(0));
        Assert.DoesNotContain('́', buffer.GetScreenLineText(1));
    }

    [Fact]
    public void RepCanRepeatWideClusterAfterADeferredWrapPosition()
    {
        var buffer = new AnsiTerminalBuffer(20, 10);

        buffer.Process(new string('A', 18) + "界");
        buffer.Process("[2b");

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
