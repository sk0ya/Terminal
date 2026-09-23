using Terminal.Buffer;

namespace Terminal.Tests;

/// <summary>
/// Shrinking the row count must not evict the live screen into the scrollback.
///
/// The host shrinks the terminal whenever a pane opens below it (Loomo's dock), and with any
/// scrollback present the screen's trailing blank rows used to fill the whole new screen while the
/// real content — the prompt and the cursor with it — was pushed into the history. ConPTY then
/// repainted the prompt into the now-blank screen, so the same line existed twice and the viewport
/// kept showing the stale history copy: typing looked like it did nothing.
/// </summary>
public sealed class AnsiTerminalBufferShrinkRowsTests
{
    private const short Columns = 32;
    private const string Prompt = "PS>";

    /// <summary>A bare prompt on a cleared screen, with lines already in the scrollback.</summary>
    private static AnsiTerminalBuffer ClearedPromptWithScrollback(short rows)
    {
        var buffer = new AnsiTerminalBuffer(Columns, rows);
        buffer.Process(string.Join("\r\n", Enumerable.Range(1, rows + 4).Select(i => $"L{i:00}")));
        buffer.Process("\u001b[2J\u001b[H");
        buffer.Process(Prompt);
        return buffer;
    }

    private static string[] ScreenLines(AnsiTerminalBuffer buffer, int rows)
        => Enumerable.Range(0, rows).Select(row => buffer.GetScreenLineText(row).TrimEnd()).ToArray();

    /// <summary>Everything the viewport can reach: scrollback followed by the screen.</summary>
    private static string[] DocumentLines(AnsiTerminalBuffer buffer)
        => buffer.CreateRenderSnapshot(showCursor: false).Lines
            .Select(line => string.Concat(line.Segments.Select(segment => segment.Text)).TrimEnd())
            .ToArray();

    private static int CountLines(string[] lines, string text)
        => lines.Count(line => line == text);

    // ----- the bug -----

    [Fact]
    public void ShrinkingRowsKeepsTheCursorLineOnScreenWhenScrollbackExists()
    {
        AnsiTerminalBuffer buffer = ClearedPromptWithScrollback(40);
        Assert.Equal(Prompt, buffer.GetScreenLineText(0).TrimEnd());

        buffer.Resize(Columns, 12);

        string[] screen = ScreenLines(buffer, 12);
        Assert.Contains(Prompt, screen);
    }

    [Fact]
    public void ShrinkingRowsLeavesTheCursorOnTheCursorLine()
    {
        AnsiTerminalBuffer buffer = ClearedPromptWithScrollback(40);

        buffer.Resize(Columns, 12);

        Assert.Equal(Prompt, buffer.GetScreenLineText(buffer.CursorRow).TrimEnd());
    }

    [Fact]
    public void ShrinkingRowsDoesNotLeaveASecondCopyOfTheCursorLine()
    {
        AnsiTerminalBuffer buffer = ClearedPromptWithScrollback(40);

        buffer.Resize(Columns, 12);

        Assert.Equal(1, CountLines(DocumentLines(buffer), Prompt));
    }

    [Fact]
    public void ShrinkingRowsKeepsTheScrollbackOutOfTheScreen()
    {
        AnsiTerminalBuffer buffer = ClearedPromptWithScrollback(40);

        buffer.Resize(Columns, 12);

        // The four lines that had scrolled off stay reachable, and stay above the screen.
        string[] document = DocumentLines(buffer);
        string[] screen = ScreenLines(buffer, 12);
        foreach (string line in new[] { "L01", "L02", "L03", "L04" })
        {
            Assert.Contains(line, document);
            Assert.DoesNotContain(line, screen);
        }
    }

    [Fact]
    public void ShrinkingRowsDropsOnlyTheBlankTail()
    {
        AnsiTerminalBuffer buffer = ClearedPromptWithScrollback(40);
        int before = DocumentLines(buffer).Length;

        buffer.Resize(Columns, 12);

        // 4 scrollback + 40 screen rows becomes 4 scrollback + 12 screen rows: only blanks go.
        int after = DocumentLines(buffer).Length;
        Assert.Equal(44, before);
        Assert.Equal(16, after);
    }

    // ----- the cases that already worked, and must keep working -----

    [Fact]
    public void ShrinkingRowsKeepsTheCursorLineOnScreenWithoutScrollback()
    {
        var buffer = new AnsiTerminalBuffer(Columns, 40);
        buffer.Process(Prompt);

        buffer.Resize(Columns, 12);

        Assert.Equal(Prompt, buffer.GetScreenLineText(0).TrimEnd());
        Assert.Equal(Prompt, buffer.GetScreenLineText(buffer.CursorRow).TrimEnd());
    }

    [Fact]
    public void ShrinkingRowsKeepsTheBottomRowsWhenTheScreenIsFull()
    {
        var buffer = new AnsiTerminalBuffer(Columns, 12);
        buffer.Process(string.Join("\r\n", Enumerable.Range(1, 12).Select(i => $"L{i:00}")));

        buffer.Resize(Columns, 10);

        Assert.Equal("L03", buffer.GetScreenLineText(0).TrimEnd());
        Assert.Equal("L12", buffer.GetScreenLineText(9).TrimEnd());
    }

    [Fact]
    public void ShrinkingRowsMovesOverflowingContentToTheScrollbackInsteadOfDroppingIt()
    {
        // Content taller than the new screen, with a blank tail under it.
        var buffer = new AnsiTerminalBuffer(Columns, 40);
        buffer.Process(string.Join("\r\n", Enumerable.Range(1, 44).Select(i => $"L{i:00}")));
        buffer.Process("\u001b[2J\u001b[H");
        buffer.Process(string.Join("\r\n", Enumerable.Range(1, 31).Select(i => $"C{i:00}")));

        buffer.Resize(Columns, 12);

        string[] document = DocumentLines(buffer);
        for (int i = 1; i <= 31; i++)
        {
            Assert.Contains($"C{i:00}", document);
        }

        // The cursor sits on the last written line, so that line stays on screen and the rows the
        // screen no longer has room for go to the scrollback rather than being dropped.
        string[] screen = ScreenLines(buffer, 12);
        Assert.Contains("C31", screen);
        Assert.Equal("C31", buffer.GetScreenLineText(buffer.CursorRow).TrimEnd());
        Assert.DoesNotContain("C01", screen);
    }

    [Fact]
    public void ShrinkingRowsKeepsContentBelowTheCursorOnScreen()
    {
        AnsiTerminalBuffer buffer = ClearedPromptWithScrollback(40);
        buffer.Process("\u001b[2J\u001b[H");
        buffer.Process(string.Join("\r\n", Enumerable.Range(1, 6).Select(i => $"C{i:00}")));
        buffer.Process("\u001b[1;1H");   // cursor back to the top, content still below it

        buffer.Resize(Columns, 12);

        string[] screen = ScreenLines(buffer, 12);
        for (int i = 1; i <= 6; i++)
        {
            Assert.Contains($"C{i:00}", screen);
        }
    }

    // ----- a column-only resize keeps the row count, so the screen must not move -----

    [Fact]
    public void ChangingOnlyColumnsKeepsThePromptAtTheTopOfTheScreen()
    {
        AnsiTerminalBuffer buffer = ClearedPromptWithScrollback(40);

        buffer.Resize(24, 40);

        Assert.Equal(Prompt, buffer.GetScreenLineText(0).TrimEnd());
        Assert.Equal(0, buffer.CursorRow);
    }

    [Fact]
    public void ChangingOnlyColumnsKeepsTheScrollbackOutOfTheScreen()
    {
        AnsiTerminalBuffer buffer = ClearedPromptWithScrollback(40);

        buffer.Resize(24, 40);

        string[] screen = ScreenLines(buffer, 40);
        Assert.DoesNotContain("L01", screen);
        Assert.Contains("L01", DocumentLines(buffer));
    }

    // ----- opening and closing a pane below the terminal, over and over -----

    [Fact]
    public void ShrinkingAndGrowingRepeatedlyKeepsASingleCopyOfTheCursorLine()
    {
        AnsiTerminalBuffer buffer = ClearedPromptWithScrollback(40);

        for (int round = 0; round < 5; round++)
        {
            buffer.Resize(Columns, 12);
            Assert.Equal(1, CountLines(DocumentLines(buffer), Prompt));
            Assert.Equal(Prompt, buffer.GetScreenLineText(buffer.CursorRow).TrimEnd());

            buffer.Resize(Columns, 40);
            Assert.Equal(1, CountLines(DocumentLines(buffer), Prompt));
            Assert.Equal(Prompt, buffer.GetScreenLineText(buffer.CursorRow).TrimEnd());
        }
    }

    [Fact]
    public void ShrinkingAndGrowingRepeatedlyDoesNotGrowTheDocument()
    {
        AnsiTerminalBuffer buffer = ClearedPromptWithScrollback(40);
        int start = DocumentLines(buffer).Length;

        for (int round = 0; round < 5; round++)
        {
            buffer.Resize(Columns, 12);
            buffer.Resize(Columns, 40);
        }

        Assert.True(
            DocumentLines(buffer).Length <= start,
            $"document grew across resize round trips: {start} -> {DocumentLines(buffer).Length}");
    }

    // ----- combinations -----

    [Fact]
    public void ShrinkingRowsAndColumnsTogetherKeepsTheCursorLineOnScreen()
    {
        AnsiTerminalBuffer buffer = ClearedPromptWithScrollback(40);

        buffer.Resize(20, 12);

        Assert.Equal(Prompt, buffer.GetScreenLineText(buffer.CursorRow).TrimEnd());
        Assert.Equal(1, CountLines(DocumentLines(buffer), Prompt));
    }

    [Fact]
    public void ShrinkingRowsKeepsAWrappedCursorLineOnScreen()
    {
        AnsiTerminalBuffer buffer = ClearedPromptWithScrollback(40);
        buffer.Process(new string('W', 50));   // wraps once at 32 columns

        buffer.Resize(Columns, 12);

        string[] screen = ScreenLines(buffer, 12);
        Assert.Contains(screen, line => line.Contains('W'));
        Assert.Contains('W', buffer.GetScreenLineText(buffer.CursorRow));
    }

    [Fact]
    public void ShrinkingRowsOnTheAlternateScreenKeepsItsContent()
    {
        AnsiTerminalBuffer buffer = ClearedPromptWithScrollback(40);
        buffer.Process("\u001b[?1049h");
        buffer.Process("alpha\r\nbeta");

        buffer.Resize(Columns, 12);

        Assert.True(buffer.IsAlternateScreenActive);
        Assert.Equal("alpha", buffer.GetScreenLineText(0).TrimEnd());
        Assert.Equal("beta", buffer.GetScreenLineText(1).TrimEnd());
    }

    [Fact]
    public void LeavingTheAlternateScreenAfterShrinkingRestoresTheCursorLine()
    {
        AnsiTerminalBuffer buffer = ClearedPromptWithScrollback(40);
        buffer.Process("\u001b[?1049h");
        buffer.Resize(Columns, 12);
        buffer.Process("\u001b[?1049l");

        string[] screen = ScreenLines(buffer, 12);
        Assert.Contains(Prompt, screen);
        Assert.Equal(1, CountLines(DocumentLines(buffer), Prompt));
    }
}
