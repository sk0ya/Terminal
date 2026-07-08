using Terminal.Rendering;

namespace Terminal.Tests;

public sealed class TerminalSelectionSearchModelTests
{
    [Fact]
    public void SelectionStateNormalizesHasSelectionAndClearResetsBlockMode()
    {
        var model = new TerminalSelectionSearchModel
        {
            Selection = new TerminalTextRange(new(1, 2), new(0, 1)),
            IsBlockSelection = true,
            BlockAnchorColumn = 4,
            BlockCurrentColumn = 1
        };

        Assert.True(model.HasSelection);
        Assert.Equal(new TerminalTextPosition(0, 1),
            TerminalSelectionSearchModel.Normalize(model.Selection)!.Value.Start);

        model.ClearSelection();

        Assert.False(model.HasSelection);
        Assert.False(model.IsBlockSelection);
        Assert.Null(model.Selection);
    }

    [Fact]
    public void NormalizeAndClampRangeHandlesReverseAndOutOfBoundsPositions()
    {
        var lines = Lines("abc", "de");
        var reverse = new TerminalTextRange(
            new TerminalTextPosition(99, 99),
            new TerminalTextPosition(-2, -3));

        TerminalTextRange normalized = TerminalSelectionSearchModel.Normalize(
            TerminalSelectionSearchModel.ClampRange(lines, reverse))!.Value;

        Assert.Equal(new TerminalTextPosition(0, 0), normalized.Start);
        Assert.Equal(new TerminalTextPosition(1, 2), normalized.End);
    }

    [Fact]
    public void TryCreateMatchRangeClampsWithoutIntegerOverflowAndPreservesSelectionOnFailure()
    {
        var lines = Lines("alpha");

        Assert.True(TerminalSelectionSearchModel.TryCreateMatchRange(
            lines, 0, int.MaxValue, int.MaxValue, out TerminalTextRange atEnd));
        Assert.True(atEnd.IsEmpty);
        Assert.Equal(5, atEnd.Start.TextIndex);
        Assert.False(TerminalSelectionSearchModel.TryCreateMatchRange(lines, 1, 0, 1, out _));
    }

    [Fact]
    public void FindMatchesIsNonOverlappingCaseAwareAndDoesNotNeedSta()
    {
        var lines = Lines("aaaa", "AaA");

        var ordinal = TerminalSelectionSearchModel.FindMatches(lines, "aa", StringComparison.Ordinal);
        var ignoreCase = TerminalSelectionSearchModel.FindMatches(lines, "aa", StringComparison.OrdinalIgnoreCase);

        Assert.Equal([(0, 0), (0, 2)], ordinal.Select(match => (match.LineIndex, match.Column)));
        Assert.Equal([(0, 0), (0, 2), (1, 0)], ignoreCase.Select(match => (match.LineIndex, match.Column)));
        Assert.Equal(ignoreCase.Count,
            TerminalSelectionSearchModel.CountMatches(lines, "aa", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TryFindNextUsesSelectionEdgeAndReportsWrappingInBothDirections()
    {
        var lines = Lines("hit x hit", "tail hit");
        var last = new TerminalTextRange(new(1, 5), new(1, 8));

        Assert.True(TerminalSelectionSearchModel.TryFindNext(
            lines, last, "hit", StringComparison.Ordinal, forward: true, out TerminalTextRange first, out bool wrapped));
        Assert.True(wrapped);
        Assert.Equal(new TerminalTextPosition(0, 0), first.Start);

        Assert.True(TerminalSelectionSearchModel.TryFindNext(
            lines, first, "hit", StringComparison.Ordinal, forward: false, out TerminalTextRange previous, out wrapped));
        Assert.True(wrapped);
        Assert.Equal(new TerminalTextPosition(1, 5), previous.Start);
    }

    [Fact]
    public void ExtractTextSupportsReverseMultilineAndWideCharacterBlockSelection()
    {
        var lines = Lines("abc", "de");
        var reverse = new TerminalTextRange(new(1, 1), new(0, 1));
        Assert.Equal("bc" + Environment.NewLine + "d",
            TerminalSelectionSearchModel.ExtractText(lines, reverse, blockSelection: false));

        var wideLines = Lines(("A界B", 4));
        var wholeLine = new TerminalTextRange(new(0, 0), new(0, 3));
        Assert.Equal("界", TerminalSelectionSearchModel.ExtractText(
            wideLines, wholeLine, blockSelection: true, blockAnchorColumn: 1, blockCurrentColumn: 3));
        Assert.Equal("界", TerminalSelectionSearchModel.ExtractText(
            wideLines, wholeLine, blockSelection: true, blockAnchorColumn: 3, blockCurrentColumn: 1));
    }

    [Fact]
    public void BlockSelectionDoesNotSplitCombiningGrapheme()
    {
        var lines = Lines(("Xa\u0301Y", 3));
        var wholeLine = new TerminalTextRange(new(0, 0), new(0, 4));

        Assert.Equal("a\u0301", TerminalSelectionSearchModel.ExtractText(
            lines, wholeLine, blockSelection: true, blockAnchorColumn: 1, blockCurrentColumn: 2));
    }

    [Theory]
    [InlineData(double.NaN, double.NaN, 0, 1)]
    [InlineData(double.NegativeInfinity, double.PositiveInfinity, int.MinValue, int.MaxValue)]
    [InlineData(double.PositiveInfinity, double.PositiveInfinity, int.MaxValue - 1, int.MaxValue)]
    [InlineData(double.NegativeInfinity, double.NegativeInfinity, int.MinValue, int.MinValue + 1)]
    [InlineData(-1.25, 2.25, -2, 3)]
    public void GetBlockColumnsClampsNonFiniteAndExtremeValues(
        double anchor,
        double current,
        int expectedLeft,
        int expectedRight)
    {
        Assert.Equal((expectedLeft, expectedRight),
            TerminalSelectionSearchModel.GetBlockColumns(anchor, current));
    }

    [Fact]
    public void EmptySnapshotAndEmptyQueryHaveNoSelectionOrMatches()
    {
        TerminalSelectionLine[] lines = [];

        Assert.Empty(TerminalSelectionSearchModel.FindMatches(lines, string.Empty, StringComparison.Ordinal));
        Assert.Equal(string.Empty, TerminalSelectionSearchModel.ExtractText(
            lines, new TerminalTextRange(new(0, 0), new(0, 1)), blockSelection: false));
        Assert.False(TerminalSelectionSearchModel.TryFindNext(
            lines, null, "x", StringComparison.Ordinal, true, out _, out _));
    }

    private static TerminalSelectionLine[] Lines(params string[] texts) => texts
        .Select(text => CreateLine(text, text.Length))
        .ToArray();

    private static TerminalSelectionLine[] Lines(params (string Text, int CellLength)[] lines) => lines
        .Select(line => CreateLine(line.Text, line.CellLength))
        .ToArray();

    private static TerminalSelectionLine CreateLine(string text, int cellLength) => new(
        text,
        TerminalTextCellMap.Create(text, cellLength, ambiguousAsWide: false));
}
