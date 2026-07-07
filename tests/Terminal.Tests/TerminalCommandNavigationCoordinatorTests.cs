using Terminal.Buffer;
using Terminal.Tabs;

namespace Terminal.Tests;

public sealed class TerminalCommandNavigationCoordinatorTests
{
    [Theory]
    [InlineData((int)ShellCommandZoneType.CommandStart)]
    [InlineData((int)ShellCommandZoneType.CommandExecuted)]
    [InlineData((int)ShellCommandZoneType.CommandDone)]
    public void ObserveIgnoresNonPromptZones(int zoneTypeValue)
    {
        var coordinator = new TerminalCommandNavigationCoordinator();

        Assert.False(coordinator.Observe((ShellCommandZoneType)zoneTypeValue, 10));
        Assert.Empty(coordinator.PromptLines);
    }

    [Fact]
    public void ObserveDeduplicatesOnlyConsecutivePromptRedraws()
    {
        var coordinator = new TerminalCommandNavigationCoordinator();

        Assert.True(coordinator.Observe(ShellCommandZoneType.PromptStart, 4));
        Assert.False(coordinator.Observe(ShellCommandZoneType.PromptStart, 4));
        Assert.True(coordinator.Observe(ShellCommandZoneType.PromptStart, 9));
        Assert.True(coordinator.Observe(ShellCommandZoneType.PromptStart, 4));

        Assert.Equal([4, 9, 4], coordinator.PromptLines);
    }

    [Fact]
    public void FindAdjacentUsesExistingInsertionOrderAndStrictComparisons()
    {
        var coordinator = new TerminalCommandNavigationCoordinator();
        coordinator.Observe(ShellCommandZoneType.PromptStart, 2);
        coordinator.Observe(ShellCommandZoneType.PromptStart, 10);
        coordinator.Observe(ShellCommandZoneType.PromptStart, 6);

        Assert.Equal(6, coordinator.FindAdjacent(currentTopLine: 10, upward: true));
        Assert.Equal(10, coordinator.FindAdjacent(currentTopLine: 6, upward: false));
        Assert.Equal(2, coordinator.FindAdjacent(currentTopLine: 6, upward: true));
    }

    [Fact]
    public void FindAdjacentReturnsNullAtBoundariesAndWhenEmpty()
    {
        var coordinator = new TerminalCommandNavigationCoordinator();

        Assert.Null(coordinator.FindAdjacent(5, upward: true));
        Assert.Null(coordinator.FindAdjacent(5, upward: false));

        coordinator.Observe(ShellCommandZoneType.PromptStart, 5);
        Assert.Null(coordinator.FindAdjacent(5, upward: true));
        Assert.Null(coordinator.FindAdjacent(5, upward: false));
    }

    [Fact]
    public void ResetSessionClearsRecordedPromptLines()
    {
        var coordinator = new TerminalCommandNavigationCoordinator();
        coordinator.Observe(ShellCommandZoneType.PromptStart, 3);

        coordinator.ResetSession();

        Assert.Empty(coordinator.PromptLines);
        Assert.Null(coordinator.FindAdjacent(4, upward: true));
    }
}
