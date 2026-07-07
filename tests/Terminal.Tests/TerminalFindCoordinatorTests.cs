using Terminal.Tabs;

namespace Terminal.Tests;

public sealed class TerminalFindCoordinatorTests
{
    [Fact]
    public void EmptyQueryClearsMatchesAndReportsPrompt()
    {
        var coordinator = new TerminalFindCoordinator();
        coordinator.UpdateCriteria("term", caseSensitive: false);
        coordinator.Refresh(Matches((1, 2)), reseek: true);

        Assert.False(coordinator.UpdateCriteria(string.Empty, caseSensitive: true));

        Assert.Equal(TerminalFindStatus.EmptyQuery, coordinator.Status);
        Assert.Equal("Type to search", coordinator.PositionText);
        Assert.Empty(coordinator.Matches);
        Assert.Equal(-1, coordinator.CurrentIndex);
        Assert.Equal(StringComparison.Ordinal, coordinator.Comparison);
    }

    [Fact]
    public void RefreshReseeksFromLastAppliedAnchor()
    {
        var coordinator = new TerminalFindCoordinator();
        coordinator.UpdateCriteria("term", caseSensitive: false);
        coordinator.Refresh(Matches((2, 4), (5, 0), (5, 10)), reseek: true);
        coordinator.Move(coordinator.Matches, forward: true);
        coordinator.MarkCurrentMatchApplied();

        coordinator.Refresh(Matches((1, 0), (5, 0), (8, 0)), reseek: true);

        Assert.Equal(1, coordinator.CurrentIndex);
        Assert.Equal(new TerminalMatch(5, 0, 4, "line-5"), coordinator.CurrentMatch);
    }

    [Fact]
    public void MoveUsesNavigatorWrappingInBothDirections()
    {
        var coordinator = new TerminalFindCoordinator();
        coordinator.UpdateCriteria("term", caseSensitive: false);
        IReadOnlyList<TerminalMatch> matches = Matches((1, 0), (2, 0));

        coordinator.Move(matches, forward: false);
        Assert.Equal(1, coordinator.CurrentIndex);
        coordinator.Move(matches, forward: true);
        Assert.Equal(0, coordinator.CurrentIndex);
        Assert.Equal("1/2", coordinator.PositionText);
    }

    [Fact]
    public void RefreshWithoutReseekPreservesAndClampsIndex()
    {
        var coordinator = new TerminalFindCoordinator();
        coordinator.UpdateCriteria("term", caseSensitive: false);
        coordinator.Move(Matches((1, 0), (2, 0), (3, 0)), forward: false);
        Assert.Equal(2, coordinator.CurrentIndex);

        coordinator.Refresh(Matches((1, 0), (2, 0)), reseek: false);

        Assert.Equal(1, coordinator.CurrentIndex);
        Assert.Equal("2/2", coordinator.PositionText);
    }

    [Fact]
    public void OutputRefreshInitializesOrClampsWithoutChangingAnchor()
    {
        var coordinator = new TerminalFindCoordinator();
        coordinator.Open();
        coordinator.UpdateCriteria("term", caseSensitive: false);
        coordinator.RefreshAfterOutputChange(Matches((4, 2), (8, 1)));

        Assert.Equal(0, coordinator.CurrentIndex);
        Assert.Equal(0, coordinator.AnchorLine);
        Assert.Equal(0, coordinator.AnchorColumn);

        coordinator.Move(coordinator.Matches, forward: false);
        coordinator.MarkCurrentMatchApplied();
        coordinator.RefreshAfterOutputChange(Matches((4, 2)));

        Assert.Equal(0, coordinator.CurrentIndex);
        Assert.Equal(8, coordinator.AnchorLine);
        Assert.Equal(1, coordinator.AnchorColumn);
    }

    [Fact]
    public void NoMatchesAndCloseResetCurrentSelectionState()
    {
        var coordinator = new TerminalFindCoordinator();
        coordinator.UpdateCriteria("missing", caseSensitive: false);
        coordinator.Refresh([], reseek: true);

        Assert.Equal(TerminalFindStatus.NoMatch, coordinator.Status);
        Assert.Equal("No match", coordinator.PositionText);

        coordinator.Close();
        Assert.Empty(coordinator.Matches);
        Assert.Equal(-1, coordinator.CurrentIndex);
    }

    [Theory]
    [InlineData((int)TerminalFindKey.Enter)]
    [InlineData((int)TerminalFindKey.F3)]
    public void ResolveKeyMovesForwardWithoutShift(int keyValue)
    {
        TerminalFindKeyAction action = TerminalFindCoordinator.ResolveKey(
            (TerminalFindKey)keyValue,
            TerminalFindKeyModifiers.Control | TerminalFindKeyModifiers.Alt);

        Assert.Equal(TerminalFindKeyActionKind.Move, action.Kind);
        Assert.True(action.Forward);
        Assert.True(action.Handled);
    }

    [Theory]
    [InlineData((int)TerminalFindKey.Enter)]
    [InlineData((int)TerminalFindKey.F3)]
    public void ResolveKeyMovesBackwardWheneverShiftIsPresent(int keyValue)
    {
        TerminalFindKeyAction action = TerminalFindCoordinator.ResolveKey(
            (TerminalFindKey)keyValue,
            TerminalFindKeyModifiers.Shift |
            TerminalFindKeyModifiers.Alt |
            TerminalFindKeyModifiers.Control |
            TerminalFindKeyModifiers.Windows);

        Assert.Equal(TerminalFindKeyActionKind.Move, action.Kind);
        Assert.False(action.Forward);
        Assert.True(action.Handled);
    }

    [Fact]
    public void ResolveKeyClosesOnEscapeRegardlessOfModifiers()
    {
        TerminalFindKeyAction action = TerminalFindCoordinator.ResolveKey(
            TerminalFindKey.Escape,
            TerminalFindKeyModifiers.Shift |
            TerminalFindKeyModifiers.Alt |
            TerminalFindKeyModifiers.Control |
            TerminalFindKeyModifiers.Windows);

        Assert.Equal(TerminalFindKeyActionKind.Close, action.Kind);
        Assert.True(action.Handled);
    }

    [Fact]
    public void ResolveKeyTogglesCaseSensitivityWhenAltIsPresentWithOtherModifiers()
    {
        TerminalFindKeyAction action = TerminalFindCoordinator.ResolveKey(
            TerminalFindKey.C,
            TerminalFindKeyModifiers.Alt |
            TerminalFindKeyModifiers.Shift |
            TerminalFindKeyModifiers.Control |
            TerminalFindKeyModifiers.Windows);

        Assert.Equal(TerminalFindKeyActionKind.ToggleCaseSensitivity, action.Kind);
        Assert.True(action.Handled);
    }

    [Theory]
    [InlineData((int)TerminalFindKey.C, (int)TerminalFindKeyModifiers.None)]
    [InlineData((int)TerminalFindKey.C, (int)TerminalFindKeyModifiers.Shift)]
    [InlineData((int)TerminalFindKey.Other, (int)TerminalFindKeyModifiers.Alt)]
    public void ResolveKeyLeavesUnsupportedInputUnhandled(int keyValue, int modifierValue)
    {
        TerminalFindKeyAction action = TerminalFindCoordinator.ResolveKey(
            (TerminalFindKey)keyValue,
            (TerminalFindKeyModifiers)modifierValue);

        Assert.Equal(TerminalFindKeyActionKind.None, action.Kind);
        Assert.False(action.Handled);
    }

    [Theory]
    [InlineData((int)TerminalFindKey.F3)]
    [InlineData((int)TerminalFindKey.Escape)]
    public void ResolveWindowKeyLeavesFindKeysUnhandledWhenClosed(int keyValue)
    {
        TerminalFindKeyAction action = TerminalFindCoordinator.ResolveWindowKey(
            (TerminalFindKey)keyValue,
            TerminalFindKeyModifiers.Shift | TerminalFindKeyModifiers.Alt,
            isOpen: false);

        Assert.Equal(TerminalFindKeyActionKind.None, action.Kind);
        Assert.False(action.Handled);
    }

    [Theory]
    [InlineData((int)TerminalFindKeyModifiers.None)]
    [InlineData((int)(TerminalFindKeyModifiers.Control |
        TerminalFindKeyModifiers.Alt |
        TerminalFindKeyModifiers.Windows))]
    public void ResolveWindowKeyMovesForwardOnF3WithoutShift(int modifierValue)
    {
        TerminalFindKeyAction action = TerminalFindCoordinator.ResolveWindowKey(
            TerminalFindKey.F3,
            (TerminalFindKeyModifiers)modifierValue,
            isOpen: true);

        Assert.Equal(TerminalFindKeyActionKind.Move, action.Kind);
        Assert.True(action.Forward);
        Assert.True(action.Handled);
    }

    [Theory]
    [InlineData((int)TerminalFindKeyModifiers.Shift)]
    [InlineData((int)(TerminalFindKeyModifiers.Shift |
        TerminalFindKeyModifiers.Control |
        TerminalFindKeyModifiers.Alt |
        TerminalFindKeyModifiers.Windows))]
    public void ResolveWindowKeyMovesBackwardOnF3WithShift(int modifierValue)
    {
        TerminalFindKeyAction action = TerminalFindCoordinator.ResolveWindowKey(
            TerminalFindKey.F3,
            (TerminalFindKeyModifiers)modifierValue,
            isOpen: true);

        Assert.Equal(TerminalFindKeyActionKind.Move, action.Kind);
        Assert.False(action.Forward);
        Assert.True(action.Handled);
    }

    [Fact]
    public void ResolveWindowKeyClosesOnEscapeWithAnyModifiers()
    {
        TerminalFindKeyAction action = TerminalFindCoordinator.ResolveWindowKey(
            TerminalFindKey.Escape,
            TerminalFindKeyModifiers.Shift |
            TerminalFindKeyModifiers.Control |
            TerminalFindKeyModifiers.Alt |
            TerminalFindKeyModifiers.Windows,
            isOpen: true);

        Assert.Equal(TerminalFindKeyActionKind.Close, action.Kind);
        Assert.True(action.Handled);
    }

    [Theory]
    [InlineData((int)TerminalFindKey.Enter, (int)TerminalFindKeyModifiers.None)]
    [InlineData((int)TerminalFindKey.C, (int)TerminalFindKeyModifiers.Alt)]
    [InlineData((int)TerminalFindKey.Other, (int)TerminalFindKeyModifiers.None)]
    public void ResolveWindowKeyLeavesOtherOpenPopupInputUnhandled(int keyValue, int modifierValue)
    {
        TerminalFindKeyAction action = TerminalFindCoordinator.ResolveWindowKey(
            (TerminalFindKey)keyValue,
            (TerminalFindKeyModifiers)modifierValue,
            isOpen: true);

        Assert.Equal(TerminalFindKeyActionKind.None, action.Kind);
        Assert.False(action.Handled);
    }

    private static IReadOnlyList<TerminalMatch> Matches(params (int Line, int Column)[] positions) =>
        positions.Select(position => new TerminalMatch(
            position.Line,
            position.Column,
            Length: 4,
            LineText: $"line-{position.Line}")).ToArray();
}
