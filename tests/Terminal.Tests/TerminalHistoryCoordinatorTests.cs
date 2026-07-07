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
    public void DisplaySegmentsPreserveTextAndGroupAdjacentHighlightRuns()
    {
        IReadOnlyList<TerminalHistoryDisplaySegment> segments =
            TerminalHistoryCoordinator.BuildDisplaySegments("git status", [0, 1, 4, 6, 7]);

        Assert.Equal(
            [
                new TerminalHistoryDisplaySegment("gi", Highlighted: true),
                new TerminalHistoryDisplaySegment("t ", Highlighted: false),
                new TerminalHistoryDisplaySegment("s", Highlighted: true),
                new TerminalHistoryDisplaySegment("t", Highlighted: false),
                new TerminalHistoryDisplaySegment("at", Highlighted: true),
                new TerminalHistoryDisplaySegment("us", Highlighted: false)
            ],
            segments);
        Assert.Equal("git status", string.Concat(segments.Select(segment => segment.Text)));
    }

    [Fact]
    public void DisplaySegmentsKeepUnmatchedAndEmptyDisplaysCompatible()
    {
        Assert.Equal(
            [new TerminalHistoryDisplaySegment("command", Highlighted: false)],
            TerminalHistoryCoordinator.BuildDisplaySegments("command", []));
        Assert.Equal(
            [new TerminalHistoryDisplaySegment(string.Empty, Highlighted: false)],
            TerminalHistoryCoordinator.BuildDisplaySegments(string.Empty, []));
        Assert.Empty(TerminalHistoryCoordinator.BuildDisplaySegments(string.Empty, [0]));
    }

    [Fact]
    public void DisplaySegmentsTreatDuplicateUnorderedAndOutOfRangeIndicesAsOldViewDid()
    {
        Assert.Equal(
            [
                new TerminalHistoryDisplaySegment("a", Highlighted: false),
                new TerminalHistoryDisplaySegment("b", Highlighted: true),
                new TerminalHistoryDisplaySegment("c", Highlighted: false),
                new TerminalHistoryDisplaySegment("de", Highlighted: true),
                new TerminalHistoryDisplaySegment("f", Highlighted: false)
            ],
            TerminalHistoryCoordinator.BuildDisplaySegments("abcdef", [4, 1, 1, -1, 9, 3]));
    }

    [Fact]
    public void DisplaySegmentsPreserveUtf16SurrogateAndCombiningMarkBoundaries()
    {
        const string display = "a\U0001F600e\u0301z";

        Assert.Equal(
            [
                new TerminalHistoryDisplaySegment("a", Highlighted: false),
                new TerminalHistoryDisplaySegment("\uD83D", Highlighted: true),
                new TerminalHistoryDisplaySegment("\uDE00e\u0301z", Highlighted: false)
            ],
            TerminalHistoryCoordinator.BuildDisplaySegments(display, [1]));

        Assert.Equal(
            [
                new TerminalHistoryDisplaySegment("a\U0001F600e", Highlighted: false),
                new TerminalHistoryDisplaySegment("\u0301", Highlighted: true),
                new TerminalHistoryDisplaySegment("z", Highlighted: false)
            ],
            TerminalHistoryCoordinator.BuildDisplaySegments(display, [4]));
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

    [Fact]
    public void SeedOnceLoadsAndMergesHistoryOnlyOnFirstAttempt()
    {
        var coordinator = new TerminalHistoryCoordinator(limit: 10);
        coordinator.Record("session");
        int loadCount = 0;

        coordinator.SeedOnce(enabled: true, () =>
        {
            loadCount++;
            return ["older", "session"];
        });
        coordinator.SeedOnce(enabled: true, () =>
        {
            loadCount++;
            return ["unexpected"];
        });

        Assert.Equal(1, loadCount);
        Assert.Equal(["older", "session"], coordinator.History);
        Assert.True(coordinator.IsSeeded);
    }

    [Fact]
    public void DisabledSeedAttemptIsConsumedWithoutLoadingHistory()
    {
        var coordinator = new TerminalHistoryCoordinator(limit: 10);
        int loadCount = 0;

        coordinator.SeedOnce(enabled: false, () =>
        {
            loadCount++;
            return ["unexpected"];
        });
        coordinator.SeedOnce(enabled: true, () =>
        {
            loadCount++;
            return ["also-unexpected"];
        });

        Assert.Equal(0, loadCount);
        Assert.Empty(coordinator.History);
        Assert.True(coordinator.IsSeeded);
    }

    [Fact]
    public void LoaderFailureConsumesSeedAttemptAndPreventsRetry()
    {
        var coordinator = new TerminalHistoryCoordinator(limit: 10);
        int loadCount = 0;

        Assert.Throws<InvalidOperationException>(() => coordinator.SeedOnce(enabled: true, () =>
        {
            loadCount++;
            throw new InvalidOperationException("read failed");
        }));

        coordinator.SeedOnce(enabled: true, () =>
        {
            loadCount++;
            return ["unexpected"];
        });

        Assert.Equal(1, loadCount);
        Assert.True(coordinator.IsSeeded);
        Assert.Empty(coordinator.History);
    }

    [Fact]
    public void SeedOncePreservesMergeOrderDeduplicationAndLimit()
    {
        var coordinator = new TerminalHistoryCoordinator(limit: 4);
        coordinator.Record("session-a");
        coordinator.Record("shared");

        coordinator.SeedOnce(
            enabled: true,
            () => ["trimmed", "old-a", "shared", "old-b", "old-a"]);

        Assert.Equal(["old-b", "old-a", "session-a", "shared"], coordinator.History);
    }

    [Theory]
    [InlineData((int)TerminalHistoryKey.Escape, (int)TerminalHistoryKeyActionKind.Close, 0)]
    [InlineData((int)TerminalHistoryKey.Enter, (int)TerminalHistoryKeyActionKind.Accept, 0)]
    [InlineData((int)TerminalHistoryKey.Up, (int)TerminalHistoryKeyActionKind.MoveSelection, -1)]
    [InlineData((int)TerminalHistoryKey.Down, (int)TerminalHistoryKeyActionKind.MoveSelection, 1)]
    public void ResolveKeyMapsUnmodifiedHistoryActions(
        int keyValue,
        int expectedKindValue,
        int expectedDelta)
    {
        TerminalHistoryKeyAction action = TerminalHistoryCoordinator.ResolveKey(
            (TerminalHistoryKey)keyValue,
            TerminalHistoryKeyModifiers.None);

        Assert.Equal((TerminalHistoryKeyActionKind)expectedKindValue, action.Kind);
        Assert.Equal(expectedDelta, action.SelectionDelta);
        Assert.True(action.Handled);
    }

    [Theory]
    [InlineData((int)TerminalHistoryKey.N, 1)]
    [InlineData((int)TerminalHistoryKey.P, -1)]
    [InlineData((int)TerminalHistoryKey.R, -1)]
    public void ResolveKeyMapsControlNavigationWithAdditionalModifiers(
        int keyValue,
        int expectedDelta)
    {
        TerminalHistoryKeyAction action = TerminalHistoryCoordinator.ResolveKey(
            (TerminalHistoryKey)keyValue,
            TerminalHistoryKeyModifiers.Control |
            TerminalHistoryKeyModifiers.Shift |
            TerminalHistoryKeyModifiers.Alt |
            TerminalHistoryKeyModifiers.Windows);

        Assert.Equal(TerminalHistoryKeyActionKind.MoveSelection, action.Kind);
        Assert.Equal(expectedDelta, action.SelectionDelta);
        Assert.True(action.Handled);
    }

    [Theory]
    [InlineData((int)TerminalHistoryKey.Escape, (int)TerminalHistoryKeyActionKind.Close, 0)]
    [InlineData((int)TerminalHistoryKey.Enter, (int)TerminalHistoryKeyActionKind.Accept, 0)]
    [InlineData((int)TerminalHistoryKey.Up, (int)TerminalHistoryKeyActionKind.MoveSelection, -1)]
    [InlineData((int)TerminalHistoryKey.Down, (int)TerminalHistoryKeyActionKind.MoveSelection, 1)]
    public void ResolveKeyKeepsDirectActionsWithAdditionalModifiers(
        int keyValue,
        int expectedKindValue,
        int expectedDelta)
    {
        TerminalHistoryKeyAction action = TerminalHistoryCoordinator.ResolveKey(
            (TerminalHistoryKey)keyValue,
            TerminalHistoryKeyModifiers.Control | TerminalHistoryKeyModifiers.Alt);

        Assert.Equal((TerminalHistoryKeyActionKind)expectedKindValue, action.Kind);
        Assert.Equal(expectedDelta, action.SelectionDelta);
        Assert.True(action.Handled);
    }

    [Theory]
    [InlineData((int)TerminalHistoryKey.N)]
    [InlineData((int)TerminalHistoryKey.P)]
    [InlineData((int)TerminalHistoryKey.R)]
    [InlineData((int)TerminalHistoryKey.Other)]
    public void ResolveKeyLeavesUnsupportedKeysUnhandled(int keyValue)
    {
        TerminalHistoryKeyAction action = TerminalHistoryCoordinator.ResolveKey(
            (TerminalHistoryKey)keyValue,
            TerminalHistoryKeyModifiers.Shift | TerminalHistoryKeyModifiers.Alt);

        Assert.Equal(TerminalHistoryKeyActionKind.None, action.Kind);
        Assert.Equal(0, action.SelectionDelta);
        Assert.False(action.Handled);
    }
}
