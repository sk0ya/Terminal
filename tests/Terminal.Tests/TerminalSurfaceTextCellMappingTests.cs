using Terminal.Rendering;

namespace Terminal.Tests;

public sealed class TerminalSurfaceTextCellMappingTests
{
    private const string MixedText = "A界😀e\u0301Z";

    [Fact]
    public void MixedUnicodeTextMapsUtf16IndicesToWholeCellSpans()
    {
        TerminalTextCellMap map = TerminalTextCellMap.Create(MixedText, targetCellLength: 7, ambiguousAsWide: false);

        Assert.Equal(0, map.GetCellColumn(0, preferTrailingEdge: false));
        Assert.Equal(1, map.GetCellColumn(1, preferTrailingEdge: false));
        Assert.Equal(3, map.GetCellColumn(2, preferTrailingEdge: false));
        Assert.Equal(3, map.GetCellColumn(3, preferTrailingEdge: false));
        Assert.Equal(5, map.GetCellColumn(3, preferTrailingEdge: true));
        Assert.Equal(5, map.GetCellColumn(4, preferTrailingEdge: false));
        Assert.Equal(5, map.GetCellColumn(5, preferTrailingEdge: false));
        Assert.Equal(6, map.GetCellColumn(5, preferTrailingEdge: true));
        Assert.Equal(6, map.GetCellColumn(6, preferTrailingEdge: false));
        Assert.Equal(7, map.GetCellColumn(7, preferTrailingEdge: false));
    }

    [Theory]
    [InlineData(0.25, 0)]
    [InlineData(0.75, 1)]
    [InlineData(1.25, 1)]
    [InlineData(2.00, 2)]
    [InlineData(2.75, 2)]
    [InlineData(3.25, 2)]
    [InlineData(4.75, 4)]
    [InlineData(5.25, 4)]
    [InlineData(5.75, 6)]
    [InlineData(6.75, 7)]
    public void CellHitsReturnOnlyTextElementBoundaries(double cellColumn, int expectedTextIndex)
    {
        TerminalTextCellMap map = TerminalTextCellMap.Create(MixedText, targetCellLength: 7, ambiguousAsWide: false);

        int textIndex = map.GetTextIndex(cellColumn);

        Assert.Equal(expectedTextIndex, textIndex);
        Assert.DoesNotContain(textIndex, new[] { 3, 5 });
    }

    [Fact]
    public void AmbiguousWidthChangesTextIndexCellBoundary()
    {
        TerminalTextCellMap narrow = TerminalTextCellMap.Create("·X", targetCellLength: 2, ambiguousAsWide: false);
        TerminalTextCellMap wide = TerminalTextCellMap.Create("·X", targetCellLength: 3, ambiguousAsWide: true);

        Assert.Equal(1, narrow.GetCellColumn(1, preferTrailingEdge: false));
        Assert.Equal(2, wide.GetCellColumn(1, preferTrailingEdge: false));
        Assert.Equal(1, wide.GetTextIndex(1.75));
    }

    [Fact]
    public void SegmentCellLengthsRemainAuthoritativeWhenMapsAreCombined()
    {
        TerminalTextCellMap map = TerminalTextCellMap.Create(
            [("A", 2), ("B", 1)],
            targetCellLength: 3,
            ambiguousAsWide: false);

        Assert.Equal(2, map.GetCellColumn(1, preferTrailingEdge: false));
        Assert.Equal(1, map.GetTextIndex(1.75));
    }

    [Fact]
    public void CoordinateMappingIncludesPaddingAndScrollOffsets()
    {
        var coordinates = new TerminalSurfaceCoordinateMapper(
            CellWidth: 8,
            CellHeight: 16,
            PaddingLeft: 11,
            PaddingTop: 7,
            HorizontalOffset: 40,
            VerticalOffset: 48);

        Assert.Equal(7.25, coordinates.GetCellColumn(29));
        Assert.Equal(4, coordinates.GetLineIndex(27, lineCount: 8));
    }

    [Theory]
    [InlineData(-10, 0)]
    [InlineData(0, 0)]
    [InlineData(999, 7)]
    public void CoordinateMappingClampsLineToSnapshot(double y, int expectedLine)
    {
        var coordinates = new TerminalSurfaceCoordinateMapper(8, 16, 0, 0, 0, 0);

        Assert.Equal(expectedLine, coordinates.GetLineIndex(y, lineCount: 8));
    }
}
