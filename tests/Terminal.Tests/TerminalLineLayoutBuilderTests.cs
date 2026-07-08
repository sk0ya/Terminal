using System.Windows.Media;

using Terminal.Buffer;
using Terminal.Rendering;

namespace Terminal.Tests;

public sealed class TerminalLineLayoutBuilderTests
{
    [Fact]
    public void CreateCombinesSegmentsAndPreservesCellOffsetsStylesAndHyperlinks()
    {
        AnsiTerminalBuffer.TerminalRenderSegmentSnapshot first = Segment(
            "A",
            1,
            Colors.Red,
            background: Colors.DarkBlue,
            bold: true,
            italic: true,
            underlineStyle: UnderlineStyle.Curly,
            underlineColor: Colors.Yellow,
            strikethrough: true,
            overline: true,
            hyperlink: "https://example.test",
            blink: true);
        AnsiTerminalBuffer.TerminalRenderSegmentSnapshot empty = Segment(
            "", 0, Colors.Green, italic: true);
        AnsiTerminalBuffer.TerminalRenderSegmentSnapshot last = Segment(
            "界x", 3, Colors.Blue, italic: true, hyperlink: "file:///tmp/x");
        var snapshot = new AnsiTerminalBuffer.TerminalRenderLineSnapshot(0, 4, [first, empty, last]);

        TerminalLineLayout layout = TerminalLineLayoutBuilder.Create(snapshot, ambiguousAsWide: false);

        Assert.Equal("A界x", layout.Text);
        Assert.Equal(4, layout.CellLength);
        Assert.Equal([0, 1, 1], layout.Segments.Select(static segment => segment.StartCell));
        Assert.Equal(first, layout.Segments[0].Snapshot);
        Assert.True(layout.Segments[0].Snapshot.Blink);
        Assert.Equal(empty, layout.Segments[1].Snapshot);
        Assert.Equal(last, layout.Segments[2].Snapshot);
        TerminalLineSegmentLayout replacement = layout.Segments[0] with { StartCell = 99 };
        _ = layout.Segments.SetItem(0, replacement);
        Assert.Equal(0, layout.Segments[0].StartCell);
        Assert.Equal(
            [
                new TerminalHyperlinkSegment(0, 1, "https://example.test"),
                new TerminalHyperlinkSegment(1, 0, null),
                new TerminalHyperlinkSegment(1, 3, "file:///tmp/x")
            ],
            layout.HyperlinkSegments.ToArray());
        Assert.Equal(1, layout.TextCellMap.GetCellColumn(1, preferTrailingEdge: false));
        Assert.Equal(3, layout.TextCellMap.GetCellColumn(2, preferTrailingEdge: true));
        Assert.True(TerminalHyperlinkDetector.TryResolve(
            layout.Text,
            layout.TextCellMap,
            layout.HyperlinkSegments,
            textIndex: 1,
            out TerminalHyperlinkMatch match));
        Assert.Equal(new TerminalHyperlinkMatch("file:///tmp/x", 1, 4), match);
    }

    [Fact]
    public void CreateSupportsAnEmptyLine()
    {
        var snapshot = new AnsiTerminalBuffer.TerminalRenderLineSnapshot(-1, 0, []);

        TerminalLineLayout layout = TerminalLineLayoutBuilder.Create(snapshot, ambiguousAsWide: false);

        Assert.Empty(layout.Text);
        Assert.Empty(layout.Segments);
        Assert.Empty(layout.HyperlinkSegments);
        Assert.Equal(0, layout.TextCellMap.CellLength);
        Assert.Equal(0, layout.TextCellMap.GetTextIndex(10));
    }

    [Fact]
    public void CreatePassesAmbiguousWidthPolicyToTextCellMapping()
    {
        var snapshot = new AnsiTerminalBuffer.TerminalRenderLineSnapshot(
            -1, 3, [Segment("·X", 3, Colors.White)]);

        TerminalLineLayout narrow = TerminalLineLayoutBuilder.Create(snapshot, ambiguousAsWide: false);
        TerminalLineLayout wide = TerminalLineLayoutBuilder.Create(snapshot, ambiguousAsWide: true);

        Assert.Equal(1, narrow.TextCellMap.GetCellColumn(1, preferTrailingEdge: false));
        Assert.Equal(2, wide.TextCellMap.GetCellColumn(1, preferTrailingEdge: false));
    }

    [Fact]
    public void CreateTreatsACombiningGraphemeAsOneMappedCell()
    {
        var snapshot = new AnsiTerminalBuffer.TerminalRenderLineSnapshot(
            -1, 2, [Segment("e\u0301", 1, Colors.White), Segment("x", 1, Colors.White)]);

        TerminalLineLayout layout = TerminalLineLayoutBuilder.Create(snapshot, ambiguousAsWide: false);

        Assert.Equal("e\u0301x", layout.Text);
        Assert.Equal(0, layout.TextCellMap.GetCellColumn(1, preferTrailingEdge: false));
        Assert.Equal(1, layout.TextCellMap.GetCellColumn(1, preferTrailingEdge: true));
        Assert.Equal(1, layout.TextCellMap.GetCellColumn(2, preferTrailingEdge: false));
    }

    [Theory]
    [InlineData(5, 0)]
    [InlineData(2, 1)]
    public void CreateCorrectsMapWhenLineCellLengthDiffersFromSegmentTotal(
        int lineCellLength,
        int expectedTextIndexAtOneAndAHalfCells)
    {
        var snapshot = new AnsiTerminalBuffer.TerminalRenderLineSnapshot(
            -1, lineCellLength, [Segment("x", 3, Colors.White)]);

        TerminalLineLayout layout = TerminalLineLayoutBuilder.Create(snapshot, ambiguousAsWide: false);

        Assert.Equal(lineCellLength, layout.CellLength);
        Assert.Equal(lineCellLength, layout.TextCellMap.CellLength);
        Assert.Equal(expectedTextIndexAtOneAndAHalfCells, layout.TextCellMap.GetTextIndex(1.5));
        Assert.Equal(lineCellLength, layout.TextCellMap.GetCellColumn(1, preferTrailingEdge: true));
    }

    private static AnsiTerminalBuffer.TerminalRenderSegmentSnapshot Segment(
        string text,
        int cellLength,
        Color foreground,
        Color? background = null,
        bool bold = false,
        bool italic = false,
        UnderlineStyle underlineStyle = UnderlineStyle.None,
        Color? underlineColor = null,
        bool strikethrough = false,
        bool overline = false,
        string? hyperlink = null,
        bool blink = false) =>
        new(
            text,
            cellLength,
            foreground,
            background ?? Colors.Black,
            bold,
            italic,
            underlineStyle,
            underlineColor,
            strikethrough,
            overline,
            hyperlink,
            blink);
}
