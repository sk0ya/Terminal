using Terminal.Rendering;

namespace Terminal.Tests;

public sealed class TerminalHyperlinkDetectorTests
{
    [Theory]
    [InlineData("Visit https://example.com/path now", 10, "https://example.com/path")]
    [InlineData("see C:\\Projects\\Loomo\\src\\Foo.cs:42 here", 12, "C:\\Projects\\Loomo\\src\\Foo.cs:42")]
    [InlineData("open \\\\server\\share\\Foo.cs:3:7 now", 12, "\\\\server\\share\\Foo.cs:3:7")]
    [InlineData("read /var/log/app.log:8 here", 10, "/var/log/app.log:8")]
    [InlineData("error at src/Foo.cs:12 col", 13, "src/Foo.cs:12")]
    [InlineData("error at ../src/Foo.cs:12 col", 14, "../src/Foo.cs:12")]
    public void ResolvesSupportedPlainTargets(string text, int textIndex, string expected)
    {
        Assert.True(TryResolve(text, textIndex, [], out TerminalHyperlinkMatch match));
        Assert.Equal(expected, match.Target);
    }

    [Theory]
    [InlineData("(https://example.com/path).", 10, "https://example.com/path")]
    [InlineData("src/Foo.cs:12,", 5, "src/Foo.cs:12")]
    public void TrimsTrailingSentencePunctuation(string text, int textIndex, string expected)
    {
        Assert.True(TryResolve(text, textIndex, [], out TerminalHyperlinkMatch match));
        Assert.Equal(expected, match.Target);
    }

    [Fact]
    public void TextIndexMustHitTrimmedTarget()
    {
        const string text = "https://example.com/.";

        Assert.False(TryResolve(text, text.Length - 1, [], out _));
    }

    [Fact]
    public void ExplicitOsc8SegmentWinsOverDetectedUrl()
    {
        const string text = "https://example.com";
        TerminalHyperlinkSegment[] segments = [new(0, text.Length, "app://explicit")];

        Assert.True(TryResolve(text, 8, segments, out TerminalHyperlinkMatch match));
        Assert.Equal("app://explicit", match.Target);
        Assert.Equal((0, text.Length), (match.StartColumn, match.EndColumn));
    }

    [Fact]
    public void ExplicitSegmentOnlyMatchesItsCellRange()
    {
        const string text = "left right";
        TerminalHyperlinkSegment[] segments = [new(0, 4, "app://left")];

        Assert.True(TryResolve(text, 3, segments, out _));
        Assert.False(TryResolve(text, 4, segments, out _));
        Assert.False(TryResolve(text, 6, segments, out _));
    }

    [Fact]
    public void UrlWinsWhenItAlsoContainsPathLikeText()
    {
        const string text = "https://host/src/Foo.cs:12";

        Assert.True(TryResolve(text, 18, [], out TerminalHyperlinkMatch match));
        Assert.Equal(text, match.Target);
    }

    [Fact]
    public void ConvertsUtf16TargetSpanToWideAndCombiningCellSpan()
    {
        const string text = "界e\u0301 https://x.test";
        TerminalTextCellMap map = TerminalTextCellMap.Create(text, targetCellLength: 18, ambiguousAsWide: false);

        Assert.True(TerminalHyperlinkDetector.TryResolve(text, map, [], 7, out TerminalHyperlinkMatch match));
        Assert.Equal("https://x.test", match.Target);
        Assert.Equal(4, match.StartColumn);
        Assert.Equal(18, match.EndColumn);
    }

    [Fact]
    public void CombiningCodeUnitHitsExplicitSegmentCell()
    {
        const string text = "e\u0301X";
        TerminalTextCellMap map = TerminalTextCellMap.Create(text, targetCellLength: 2, ambiguousAsWide: false);
        TerminalHyperlinkSegment[] segments = [new(0, 1, "app://grapheme")];

        Assert.True(TerminalHyperlinkDetector.TryResolve(text, map, segments, 1, out TerminalHyperlinkMatch match));
        Assert.Equal("app://grapheme", match.Target);
        Assert.Equal((0, 1), (match.StartColumn, match.EndColumn));
    }

    [Fact]
    public void RejectsTextIndicesOutsideLineBounds()
    {
        const string text = "just plain words here";

        Assert.False(TryResolve(text, -1, [], out _));
        Assert.False(TryResolve(text, text.Length, [], out _));
    }

    [Fact]
    public void RejectsEmptyText()
    {
        Assert.False(TryResolve(string.Empty, 0, [], out _));
    }

    private static bool TryResolve(
        string text,
        int textIndex,
        IReadOnlyList<TerminalHyperlinkSegment> segments,
        out TerminalHyperlinkMatch match)
    {
        TerminalTextCellMap map = TerminalTextCellMap.Create(text, text.Length, ambiguousAsWide: false);
        return TerminalHyperlinkDetector.TryResolve(text, map, segments, textIndex, out match);
    }
}
