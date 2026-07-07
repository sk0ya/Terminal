using Terminal.Tabs;

namespace Terminal.Tests;

public sealed class TerminalHistoryCoordinatorTests
{
    [Fact]
    public void RecordDeduplicatesByMovingToNewestAndEnforcesLimit()
    {
        var coordinator = new TerminalHistoryCoordinator(limit: 3);

        Assert.True(coordinator.Record("one"));
        Assert.True(coordinator.Record("two"));
        Assert.True(coordinator.Record("one"));
        Assert.False(coordinator.Record("one"));
        Assert.True(coordinator.Record("three"));
        Assert.True(coordinator.Record("four"));

        Assert.Equal(["one", "three", "four"], coordinator.History);
    }

    [Fact]
    public void SeedMergeKeepsMostRecentOccurrenceAndOrder()
    {
        var coordinator = new TerminalHistoryCoordinator(limit: 10);
        coordinator.Record("session-a");
        coordinator.Record("shared");

        coordinator.MergeSeedHistory(["old-a", "shared", "old-b", "old-a"]);

        Assert.Equal(["old-b", "old-a", "session-a", "shared"], coordinator.History);
    }

    [Fact]
    public void SearchRanksBestAtBottomAndBreaksScoreTieByRecency()
    {
        var coordinator = new TerminalHistoryCoordinator(limit: 10);
        coordinator.Record("git status");
        coordinator.Record("git stash");
        coordinator.Record("echo git status");

        coordinator.Search("gits");

        Assert.Equal(3, coordinator.Results.Count);
        Assert.Equal("git stash", coordinator.Results[^1].Command);
        Assert.Equal(2, coordinator.SelectedIndex);
        Assert.Equal("3/3", coordinator.CountText);
        Assert.NotEmpty(coordinator.Results[^1].MatchedIndices);
    }

    [Fact]
    public void EmptySearchShowsAllOldestToNewestAndSelectsNewest()
    {
        var coordinator = new TerminalHistoryCoordinator(limit: 10);
        coordinator.Record("one");
        coordinator.Record("two");

        coordinator.Search(string.Empty);

        Assert.Equal(["one", "two"], coordinator.Results.Select(result => result.Command));
        Assert.Equal("two", coordinator.AcceptSelection());
        Assert.Equal("2/2", coordinator.CountText);
    }

    [Fact]
    public void SelectionMovementClampsAndAcceptsSelectedCommand()
    {
        var coordinator = new TerminalHistoryCoordinator(limit: 10);
        coordinator.Record("one");
        coordinator.Record("two");
        coordinator.Search(string.Empty);

        Assert.Equal(0, coordinator.MoveSelection(-99));
        Assert.Equal("one", coordinator.AcceptSelection());
        Assert.Equal(1, coordinator.MoveSelection(99));
        Assert.Equal("two", coordinator.AcceptSelection());
    }

    [Fact]
    public void ExternallySelectedIndexIsUsedAsKeyboardMovementOrigin()
    {
        var coordinator = new TerminalHistoryCoordinator(limit: 10);
        coordinator.Record("zero");
        coordinator.Record("one");
        coordinator.Record("two");
        coordinator.Search(string.Empty);

        coordinator.SelectIndex(1);
        int next = coordinator.MoveSelection(-1);

        Assert.Equal(0, next);
        Assert.Equal("zero", coordinator.AcceptSelection());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void MovingFromNoSelectionSelectsFirstResult(int delta)
    {
        var coordinator = new TerminalHistoryCoordinator(limit: 10);
        coordinator.Record("zero");
        coordinator.Record("one");
        coordinator.Search(string.Empty);

        coordinator.SelectIndex(-1);

        Assert.Equal(-1, coordinator.SelectedIndex);
        Assert.Null(coordinator.AcceptSelection());
        Assert.Equal(0, coordinator.MoveSelection(delta));
        Assert.Equal("zero", coordinator.AcceptSelection());
    }

    [Fact]
    public void SeedingCanBeginOnlyOnce()
    {
        var coordinator = new TerminalHistoryCoordinator(limit: 10);

        Assert.True(coordinator.TryBeginSeed());
        Assert.False(coordinator.TryBeginSeed());
        Assert.True(coordinator.IsSeeded);
    }
}
