using Terminal.Rendering;
using Terminal.Tabs;

namespace Terminal.Tests;

public sealed class TerminalRegexFindTests
{
    private static IReadOnlyList<TerminalSelectionLine> Lines(params string[] texts) =>
        texts.Select(text => new TerminalSelectionLine(text, TerminalTextCellMap.Create(text, text.Length, ambiguousAsWide: false))).ToList();

    [Fact]
    public void RegexMatchesEveryOccurrencePerRowWithItsLength()
    {
        var pattern = TerminalSelectionSearchModel.TryCreatePattern(@"err(or)?\b", caseSensitive: false, out string? error)!;
        Assert.Null(error);

        var matches = TerminalSelectionSearchModel.FindMatches(Lines("ok", "ERROR: err here", "fine"), pattern);

        Assert.Equal(
            [new TerminalMatch(1, 0, 5, "ERROR: err here"), new TerminalMatch(1, 7, 3, "ERROR: err here")],
            matches);
    }

    [Fact]
    public void EmptyMatchesAreSkipped()
    {
        var pattern = TerminalSelectionSearchModel.TryCreatePattern("x*", caseSensitive: true, out _)!;
        var matches = TerminalSelectionSearchModel.FindMatches(Lines("abxxc"), pattern);
        Assert.Equal([new TerminalMatch(0, 2, 2, "abxxc")], matches);
    }

    [Fact]
    public void InvalidPatternReportsAnError()
    {
        Assert.Null(TerminalSelectionSearchModel.TryCreatePattern("(unclosed", caseSensitive: false, out string? error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void CoordinatorShowsInvalidPatternAndClearsMatches()
    {
        var coordinator = new TerminalFindCoordinator();
        Assert.True(coordinator.UpdateCriteria("a+", caseSensitive: false, useRegex: true));
        Assert.NotNull(coordinator.Pattern);
        coordinator.Refresh([new TerminalMatch(0, 0, 1, "a")], reseek: true);

        Assert.False(coordinator.UpdateCriteria("[", caseSensitive: false, useRegex: true));
        Assert.Equal(TerminalFindStatus.InvalidPattern, coordinator.Status);
        Assert.Equal("Invalid pattern", coordinator.PositionText);
        Assert.Empty(coordinator.Matches);

        // The same text is fine as a plain search.
        Assert.True(coordinator.UpdateCriteria("[", caseSensitive: false, useRegex: false));
        Assert.Null(coordinator.Pattern);
    }

    [Fact]
    public void AltRTogglesRegex()
    {
        Assert.Equal(
            TerminalFindKeyActionKind.ToggleRegex,
            TerminalFindCoordinator.ResolveKey(TerminalFindKey.R, TerminalFindKeyModifiers.Alt).Kind);
        Assert.False(TerminalFindCoordinator.ResolveKey(TerminalFindKey.R, TerminalFindKeyModifiers.None).Handled);
    }
}
