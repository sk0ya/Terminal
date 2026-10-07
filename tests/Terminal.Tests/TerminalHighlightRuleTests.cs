using System.Text.Json;
using System.Windows.Media;

using Terminal.Rendering;
using Terminal.Settings;

namespace Terminal.Tests;

public sealed class TerminalHighlightRuleTests
{
    [Fact]
    public void ParseReadsColourAndPatternAndSkipsCommentsAndBlanks()
    {
        var rules = TerminalHighlightRule.Parse(
            ["", "// comment", @"#FF5555 \b(error|failed)\b", "  #8055FF55   warn  "],
            out IReadOnlyList<string> errors);

        Assert.Empty(errors);
        Assert.Equal(2, rules.Count);
        Assert.Equal(Color.FromRgb(0xFF, 0x55, 0x55), rules[0].Color);
        Assert.Matches(rules[0].Pattern, "Build FAILED");
        Assert.Equal(Color.FromArgb(0x80, 0x55, 0xFF, 0x55), rules[1].Color);
        Assert.Equal("warn", rules[1].Pattern.ToString());
    }

    [Theory]
    [InlineData("red error")]
    [InlineData("#FF5555")]
    [InlineData("#XYZXYZ error")]
    [InlineData("#FF5555 (unclosed")]
    public void ParseReportsAndSkipsBadLines(string line)
    {
        var rules = TerminalHighlightRule.Parse([line], out IReadOnlyList<string> errors);
        Assert.Empty(rules);
        Assert.Single(errors);
    }

    [Fact]
    public void HighlighterFindsEverySpanOfEveryRuleAndCachesByText()
    {
        var highlighter = new TerminalHighlighter();
        highlighter.SetRules(TerminalHighlightRule.Parse(["#FF0000 err", "#00FF00 ok"], out _));

        string text = "ok err ok";
        TerminalHighlightSpan[] spans = highlighter.GetSpans(text);

        Assert.Equal(
            [
                new TerminalHighlightSpan(3, 6, Color.FromRgb(0xFF, 0, 0)),
                new TerminalHighlightSpan(0, 2, Color.FromRgb(0, 0xFF, 0)),
                new TerminalHighlightSpan(7, 9, Color.FromRgb(0, 0xFF, 0)),
            ],
            spans);
        Assert.Same(spans, highlighter.GetSpans(text));
        // A rebuilt row with the same text (a different string instance) hits the cache too.
        Assert.Same(spans, highlighter.GetSpans(new string(text.AsSpan())));
        Assert.Equal(1, highlighter.CachedRowCount);

        highlighter.SetRules([]);
        Assert.False(highlighter.HasRules);
        Assert.Empty(highlighter.GetSpans(text));
    }

    [Fact]
    public void HighlightRulesSurviveJsonRoundTrip()
    {
        var settings = new TerminalAppSettings { HighlightRules = ["#FF5555 error"] };
        var restored = JsonSerializer.Deserialize<TerminalAppSettings>(JsonSerializer.Serialize(settings));
        Assert.Equal(["#FF5555 error"], restored!.HighlightRules);
        Assert.Empty(new TerminalAppSettings().HighlightRules);
    }
}
