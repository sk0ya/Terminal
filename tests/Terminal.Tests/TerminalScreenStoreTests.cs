using Terminal.Buffer;

namespace Terminal.Tests;

public sealed class TerminalScreenStoreTests
{
    [Fact]
    public void BufferNormalizesScrollbackLimitAgainstRequestedRowsBeforeViewportClamp()
    {
        var buffer = new AnsiTerminalBuffer(columns: 20, rows: 2, scrollbackLimit: 2);
        buffer.Process(string.Join("\r\n", Enumerable.Range(0, 15).Select(index => $"line-{index}")));

        Assert.Equal(2, buffer.ScrollbackLineCount);
    }

    [Fact]
    public void AppendScrollbackEnforcesLimitAndReportsRemovedCount()
    {
        // The caller supplies an already-normalized limit; Store does not raise it to viewport rows.
        var store = new TerminalScreenStore(rows: 4, columns: 4, scrollbackLimit: 2);

        Assert.Equal(0, store.AppendScrollback(Line(4, "A")));
        Assert.Equal(0, store.AppendScrollback(Line(4, "B")));
        Assert.Equal(1, store.AppendScrollback(Line(4, "C")));

        Assert.Equal(["B", "C"], store.Scrollback.Select(Text));
    }

    [Fact]
    public void FullScreenScrollUpAppendsClonesAndReturnsMutation()
    {
        var store = new TerminalScreenStore(rows: 3, columns: 4, scrollbackLimit: 10);
        store.Screen[0] = Line(4, "A");
        store.Screen[1] = Line(4, "B");
        store.Screen[2] = Line(4, "C");

        TerminalScreenMutation mutation = store.ScrollUp(
            lines: 1, top: 0, bottom: 2, columns: 4, TerminalStyle.Default, appendToScrollback: true);
        store.Screen[1].Cells[0] = Cell("X");

        Assert.True(mutation.ScrollbackChanged);
        Assert.Equal("A", Text(store.Scrollback[0]));
        Assert.Equal(["B", "X", ""], store.Screen.Select(Text));
    }

    [Fact]
    public void RegionScrollDoesNotAppendScrollback()
    {
        var store = new TerminalScreenStore(rows: 4, columns: 4, scrollbackLimit: 10);
        for (int row = 0; row < 4; row++)
        {
            store.Screen[row] = Line(4, row.ToString());
        }

        TerminalScreenMutation mutation = store.ScrollUp(
            lines: 1, top: 1, bottom: 2, columns: 4, TerminalStyle.Default, appendToScrollback: false);

        Assert.False(mutation.ScrollbackChanged);
        Assert.Empty(store.Scrollback);
        Assert.Equal(["0", "2", "", "3"], store.Screen.Select(Text));
    }

    [Fact]
    public void InsertAndDeleteLinesOperateWithinScrollRegion()
    {
        var store = new TerminalScreenStore(rows: 4, columns: 4, scrollbackLimit: 10);
        for (int row = 0; row < 4; row++)
        {
            store.Screen[row] = Line(4, row.ToString());
        }

        store.InsertLines(cursorRow: 1, scrollTop: 1, scrollBottom: 3, count: 1, columns: 4, TerminalStyle.Default);
        Assert.Equal(["0", "", "1", "2"], store.Screen.Select(Text));

        store.DeleteLines(cursorRow: 1, scrollTop: 1, scrollBottom: 3, count: 1, columns: 4, TerminalStyle.Default);
        Assert.Equal(["0", "1", "2", ""], store.Screen.Select(Text));
    }

    [Fact]
    public void EnterAndExitAlternateScreenRestoreClonedPrimaryCollection()
    {
        var store = new TerminalScreenStore(rows: 2, columns: 4, scrollbackLimit: 10);
        store.Screen[0] = Line(4, "main");

        Assert.True(store.EnterAlternateScreen(rows: 2, columns: 4));
        store.Screen[0] = Line(4, "alt");
        Assert.True(store.ExitAlternateScreen());

        Assert.Equal("main", Text(store.Screen[0]));
        Assert.False(store.ExitAlternateScreen());
    }

    [Fact]
    public void PendingPrimaryScreenCanBePromotedAfterCurrentScreenChanges()
    {
        var store = new TerminalScreenStore(rows: 2, columns: 4, scrollbackLimit: 10);
        store.Screen[0] = Line(4, "main");
        store.CapturePendingPrimaryScreen();
        store.Screen[0] = Line(4, "work");

        store.PromotePendingOrCapturePrimaryScreen();
        store.Screen[0] = Line(4, "alt");
        Assert.True(store.ExitAlternateScreen());

        Assert.Equal("main", Text(store.Screen[0]));
    }

    [Fact]
    public void ApplyReflowAndClearScrollbackMutateOwnedCollections()
    {
        var store = new TerminalScreenStore(rows: 2, columns: 4, scrollbackLimit: 10);

        store.ApplyReflow([Line(4, "new")], [Line(4, "old")]);

        Assert.Equal(["new"], store.Screen.Select(Text));
        Assert.Equal(["old"], store.Scrollback.Select(Text));

        store.ClearScrollback();
        Assert.Empty(store.Scrollback);
    }

    [Fact]
    public void PlaceCellOverWideContinuationClearsTheWholePreviousCell()
    {
        var store = new TerminalScreenStore(rows: 1, columns: 4, scrollbackLimit: 0);
        store.PlaceCell(0, 1, "界", width: 2, columns: 4, TerminalStyle.Default, hyperlink: null);

        store.PlaceCell(0, 2, "X", width: 1, columns: 4, TerminalStyle.Default, hyperlink: null);

        Assert.Equal(" ", store.Screen[0].Cells[1].Text);
        Assert.False(store.Screen[0].Cells[1].IsContinuation);
        Assert.Equal("X", store.Screen[0].Cells[2].Text);
        Assert.False(store.Screen[0].Cells[2].IsContinuation);
    }

    [Fact]
    public void EraseCharactersUsesCurrentBlankStyle()
    {
        var store = new TerminalScreenStore(rows: 1, columns: 4, scrollbackLimit: 0);
        store.Screen[0] = Line(4, "ABCD");
        TerminalStyle eraseStyle = TerminalStyle.Default with { Bold = true };

        store.EraseCharacters(row: 0, column: 1, count: 2, columns: 4, eraseStyle);

        Assert.Equal("A  D", TerminalLineSnapshotBuilder.ExtractPlainText(store.Screen[0]));
        Assert.Equal(eraseStyle, store.Screen[0].Cells[1].Style);
        Assert.Equal(eraseStyle, store.Screen[0].Cells[2].Style);
    }

    [Fact]
    public void FillRangeCanClearRegionWithoutClearingWrappedFlag()
    {
        var store = new TerminalScreenStore(rows: 1, columns: 4, scrollbackLimit: 0);
        store.Screen[0] = Line(4, "ABCD");
        store.Screen[0].IsWrapped = true;

        store.FillRange(0, 1, 3, 4, TerminalStyle.Default);

        Assert.Equal("A  D", TerminalLineSnapshotBuilder.ExtractPlainText(store.Screen[0]));
        Assert.True(store.Screen[0].IsWrapped);

        store.FillRange(0, 0, 4, 4, TerminalStyle.Default, clearWrapped: true);
        Assert.False(store.Screen[0].IsWrapped);
    }

    [Fact]
    public void FillAlignmentResetsEveryCellAndWrappedFlag()
    {
        var store = new TerminalScreenStore(rows: 2, columns: 3, scrollbackLimit: 0);
        store.Screen[0].IsWrapped = true;

        store.FillAlignment();

        Assert.All(store.Screen, line =>
        {
            Assert.Equal("EEE", Text(line));
            Assert.False(line.IsWrapped);
        });
    }

    private static TerminalLine Line(int columns, string text)
    {
        var line = new TerminalLine(columns, TerminalStyle.Default);
        for (int index = 0; index < text.Length; index++)
        {
            line.Cells[index] = Cell(text[index].ToString());
        }

        return line;
    }

    private static TerminalCell Cell(string text) =>
        new(text, TerminalStyle.Default, Hyperlink: null, IsContinuation: false, Width: 1);

    private static string Text(TerminalLine line) =>
        TerminalLineSnapshotBuilder.ExtractPlainText(line).TrimEnd();
}
