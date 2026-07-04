using System.Windows.Media;

using Terminal.Buffer;

namespace Terminal.Tests;

public sealed class TerminalLineSnapshotBuilderTests
{
    [Fact]
    public void ExtractPlainTextSkipsWideContinuationCells()
    {
        var line = new TerminalLine(4, TerminalStyle.Default);
        line.Cells[0] = Cell("A");
        line.Cells[1] = Cell("界", width: 2);
        line.Cells[2] = Cell(string.Empty, isContinuation: true, width: 0);

        string text = TerminalLineSnapshotBuilder.ExtractPlainText(line);

        Assert.Equal("A界 ", text);
    }

    [Fact]
    public void CreateSnapshotGroupsEqualStylesAndTracksCellWidth()
    {
        var line = new TerminalLine(4, TerminalStyle.Default);
        TerminalStyle red = TerminalStyle.Default with { Foreground = Colors.Red };
        TerminalStyle blue = TerminalStyle.Default with { Foreground = Colors.Blue, Bold = true };
        line.Cells[0] = Cell("A", red);
        line.Cells[1] = Cell("界", red, width: 2);
        line.Cells[2] = Cell(string.Empty, red, isContinuation: true, width: 0);
        line.Cells[3] = Cell("B", blue);

        AnsiTerminalBuffer.TerminalRenderLineSnapshot snapshot = CreateSnapshot(line);

        Assert.Equal(4, snapshot.CellLength);
        Assert.Collection(
            snapshot.Segments,
            segment =>
            {
                Assert.Equal("A界", segment.Text);
                Assert.Equal(3, segment.CellLength);
                Assert.Equal(Colors.Red, segment.Foreground);
            },
            segment =>
            {
                Assert.Equal("B", segment.Text);
                Assert.Equal(1, segment.CellLength);
                Assert.Equal(Colors.Blue, segment.Foreground);
                Assert.True(segment.Bold);
            });
    }

    [Fact]
    public void CreateSnapshotSplitsAtCursorAndReportsAnchorSegment()
    {
        var line = new TerminalLine(2, TerminalStyle.Default);
        line.Cells[0] = Cell("A");
        line.Cells[1] = Cell("B");

        AnsiTerminalBuffer.TerminalRenderLineSnapshot snapshot = TerminalLineSnapshotBuilder.CreateSnapshot(
            line,
            cursorColumn: 1,
            anchorColumn: 1,
            showCursor: true,
            screenReverse: false,
            defaultForeground: Colors.White,
            defaultBackground: Colors.Black,
            cursorAccent: Colors.Orange);

        Assert.Equal(1, snapshot.AnchorSegmentIndex);
        Assert.Equal(2, snapshot.Segments.Length);
        Assert.Equal(Colors.Black, snapshot.Segments[1].Foreground);
        Assert.Equal(Colors.White, snapshot.Segments[1].Background);
    }

    private static AnsiTerminalBuffer.TerminalRenderLineSnapshot CreateSnapshot(TerminalLine line) =>
        TerminalLineSnapshotBuilder.CreateSnapshot(
            line,
            cursorColumn: -1,
            anchorColumn: -1,
            showCursor: false,
            screenReverse: false,
            defaultForeground: Colors.White,
            defaultBackground: Colors.Black,
            cursorAccent: Colors.Orange);

    private static TerminalCell Cell(
        string text,
        TerminalStyle? style = null,
        bool isContinuation = false,
        int width = 1) =>
        new(text, style ?? TerminalStyle.Default, Hyperlink: null, isContinuation, width);
}
