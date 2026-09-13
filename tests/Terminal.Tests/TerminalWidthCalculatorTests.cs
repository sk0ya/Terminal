using System.Text;

using Terminal.Buffer;

namespace Terminal.Tests;

public sealed class TerminalWidthCalculatorTests
{
    [Theory]
    [InlineData(0x0041, false, 1)] // A
    [InlineData(0x754C, false, 2)] // 界
    [InlineData(0x1F600, false, 2)] // 😀
    [InlineData(0x0301, false, 0)] // combining acute accent
    [InlineData(0x200D, false, 0)] // ZWJ
    [InlineData(0x00B7, false, 1)] // ambiguous, narrow by default
    [InlineData(0x00B7, true, 2)] // ambiguous, configured wide
    public void ScalarWidthUsesTheSharedTerminalRule(int codePoint, bool ambiguousAsWide, int expected)
    {
        Assert.Equal(expected, TerminalWidthCalculator.GetWidth(new Rune(codePoint), ambiguousAsWide));
    }

    [Theory]
    [InlineData("e\u0301", false, 1)]
    [InlineData("👩‍💻", false, 2)]
    [InlineData("🇯🇵", false, 2)]
    [InlineData("1️⃣", false, 2)]
    [InlineData("#️⃣", false, 2)]
    [InlineData("♥️", false, 2)]
    [InlineData("♥︎", false, 1)]
    [InlineData("A\uFE0F", false, 1)]
    [InlineData("·\uFE0F", false, 1)]
    [InlineData("👨‍👩‍👧‍👦", false, 2)]
    [InlineData("👍🏽", false, 2)]
    [InlineData("·", false, 1)]
    [InlineData("·", true, 2)]
    public void GraphemeWidthUsesTheMaximumVisibleScalarWidth(string text, bool ambiguousAsWide, int expected)
    {
        Assert.Equal(expected, TerminalWidthCalculator.EstimateGraphemeWidth(text.AsSpan(), ambiguousAsWide));
    }

    [Theory]
    [InlineData("1️⃣X", 3)]
    [InlineData("♥️X", 3)]
    [InlineData("♥︎X", 2)]
    [InlineData("A\uFE0FX", 2)]
    [InlineData("·\uFE0FX", 2)]
    [InlineData("👨‍👩‍👧‍👦X", 3)]
    public void BufferUsesClusterWidthForCursorAndSnapshot(string text, int expectedCursorColumn)
    {
        var buffer = new AnsiTerminalBuffer(40, 5);
        buffer.Process(text);

        Assert.Equal(expectedCursorColumn, buffer.CursorColumn);
        AnsiTerminalBuffer.TerminalRenderLineSnapshot line =
            buffer.CreateRenderSnapshot(showCursor: false).Lines[0];
        Assert.Equal(expectedCursorColumn, line.CellLength);
        Assert.Equal(text, line.Segments.Single().Text);
    }

    [Fact]
    public void PresentationSelectorsAndKeycapMarksRemainCorrectAcrossInputChunks()
    {
        var buffer = new AnsiTerminalBuffer(40, 5);

        buffer.Process("1");
        Assert.Equal(1, buffer.CursorColumn);

        buffer.Process("\uFE0F");
        buffer.Process("\u20E3X");

        Assert.Equal(3, buffer.CursorColumn);
        AnsiTerminalBuffer.TerminalRenderLineSnapshot line =
            buffer.CreateRenderSnapshot(showCursor: false).Lines[0];
        Assert.Equal(3, line.CellLength);
        Assert.Equal("1️⃣X", line.Segments.Single().Text);
    }

    [Fact]
    public void BufferSnapshotCellLengthMatchesTheSharedWidthRule()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);
        buffer.Process("A界😀e\u0301·");

        AnsiTerminalBuffer.TerminalRenderLineSnapshot line =
            buffer.CreateRenderSnapshot(showCursor: false).Lines[0];

        Assert.Equal(7, line.CellLength);
        Assert.Equal(line.CellLength, line.Segments.Sum(static segment => segment.CellLength));
    }

    [Fact]
    public void AmbiguousWidthModeIsReflectedInNewSnapshots()
    {
        var buffer = new AnsiTerminalBuffer(32, 10)
        {
            AmbiguousWidthIsWide = true
        };
        buffer.Process("·X");

        AnsiTerminalBuffer.TerminalRenderLineSnapshot line =
            buffer.CreateRenderSnapshot(showCursor: false).Lines[0];

        Assert.Equal(3, line.CellLength);
        Assert.Equal(3, buffer.CursorColumn);
    }

    [Fact]
    public void ChangingAmbiguousWidthReflowsExistingCellsAndCursor()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);
        buffer.Process("·X");

        buffer.AmbiguousWidthIsWide = true;

        AnsiTerminalBuffer.TerminalRenderLineSnapshot wide =
            buffer.CreateRenderSnapshot(showCursor: false).Lines[0];
        Assert.Equal(3, wide.CellLength);
        Assert.Equal(3, buffer.CursorColumn);
        Assert.Equal("·X", buffer.GetScreenLineText(0).TrimEnd());

        buffer.AmbiguousWidthIsWide = false;

        AnsiTerminalBuffer.TerminalRenderLineSnapshot narrow =
            buffer.CreateRenderSnapshot(showCursor: false).Lines[0];
        Assert.Equal(2, narrow.CellLength);
        Assert.Equal(2, buffer.CursorColumn);
    }

    [Fact]
    public void ParserAndWidthStateSurviveChunkBoundaries()
    {
        const string input = "\u001b[31mA界👩\u200d💻🇯🇵e\u0301\u001b[0m·X";
        string[] chunks =
        [
            "\u001b[3",
            "1mA",
            "界👩",
            "\u200d💻🇯",
            "🇵e",
            "\u0301\u001b[0",
            "m·X"
        ];

        var singleChunk = new AnsiTerminalBuffer(32, 10);
        singleChunk.Process(input);

        var splitChunks = new AnsiTerminalBuffer(32, 10);
        foreach (string chunk in chunks)
        {
            splitChunks.Process(chunk);
        }

        Assert.Equal(singleChunk.CursorRow, splitChunks.CursorRow);
        Assert.Equal(singleChunk.CursorColumn, splitChunks.CursorColumn);
        Assert.Equal(singleChunk.CreatePlainTextSnapshot(), splitChunks.CreatePlainTextSnapshot());

        AnsiTerminalBuffer.TerminalRenderSnapshot expected = singleChunk.CreateRenderSnapshot(false);
        AnsiTerminalBuffer.TerminalRenderSnapshot actual = splitChunks.CreateRenderSnapshot(false);
        Assert.Equal(expected.Lines.Length, actual.Lines.Length);
        for (int index = 0; index < expected.Lines.Length; index++)
        {
            Assert.True(expected.Lines[index].ContentEquals(actual.Lines[index]), $"line {index} differs");
        }
    }
}
