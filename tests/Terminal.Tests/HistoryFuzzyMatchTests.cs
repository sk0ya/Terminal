using Terminal.Tabs;

namespace Terminal.Tests;

public sealed class HistoryFuzzyMatchTests
{
    [Theory]
    [InlineData("git status", "gs")]
    [InlineData("git status", "status")]
    [InlineData("git status", "gitstat")]
    [InlineData("dotnet build", "db")]
    public void TryFuzzyMatchAcceptsSubsequences(string text, string query)
    {
        Assert.True(TerminalTabView.TryFuzzyMatch(text, query, out int score, out var indices));
        Assert.True(score > 0);
        Assert.Equal(query.Replace(" ", string.Empty).Length, indices.Count);
    }

    [Theory]
    [InlineData("git status", "xyz")]
    [InlineData("git status", "sg")] // out of order
    [InlineData("git", "github")] // query longer than any subsequence
    public void TryFuzzyMatchRejectsNonSubsequences(string text, string query)
    {
        Assert.False(TerminalTabView.TryFuzzyMatch(text, query, out int score, out _));
        Assert.Equal(0, score);
    }

    [Fact]
    public void TryFuzzyMatchReportsMatchedIndicesForHighlighting()
    {
        Assert.True(TerminalTabView.TryFuzzyMatch("git status", "gs", out _, out var indices));

        // 'g' at index 0, 's' at the start of "status" (index 4).
        Assert.Equal(new[] { 0, 4 }, indices);
    }

    [Fact]
    public void TryFuzzyMatchScoresWordBoundaryHigherThanMidWord()
    {
        // "s" after a space (word boundary) should outscore "s" buried mid-word.
        Assert.True(TerminalTabView.TryFuzzyMatch("git status", "s", out int boundaryScore, out _));
        Assert.True(TerminalTabView.TryFuzzyMatch("classes", "s", out int midWordScore, out _));

        Assert.True(boundaryScore > midWordScore);
    }

    [Fact]
    public void TryFuzzyMatchIsCaseInsensitive()
    {
        Assert.True(TerminalTabView.TryFuzzyMatch("Git Status", "gs", out _, out var indices));
        Assert.Equal(2, indices.Count);
    }
}
