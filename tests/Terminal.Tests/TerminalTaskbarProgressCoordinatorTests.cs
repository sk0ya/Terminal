using Terminal.Tabs;

namespace Terminal.Tests;

public sealed class TerminalTaskbarProgressCoordinatorTests
{
    [Theory]
    [InlineData(1, TaskbarProgressState.Normal)]
    [InlineData(2, TaskbarProgressState.Error)]
    [InlineData(4, TaskbarProgressState.Warning)]
    public void DeterminateOscStatesPreserveProgress(
        int stateCode,
        TaskbarProgressState expectedState)
    {
        var coordinator = new TerminalTaskbarProgressCoordinator();

        TerminalTaskbarProgress result = coordinator.ApplyOscProgress(stateCode, 73);

        Assert.Equal(expectedState, result.State);
        Assert.Equal(73, result.Progress);
        Assert.Equal(result, coordinator.Current);
    }

    [Theory]
    [InlineData(0, TaskbarProgressState.None)]
    [InlineData(3, TaskbarProgressState.Indeterminate)]
    [InlineData(99, TaskbarProgressState.None)]
    public void NonDeterminateOscStatesDiscardProgress(
        int stateCode,
        TaskbarProgressState expectedState)
    {
        var coordinator = new TerminalTaskbarProgressCoordinator();

        TerminalTaskbarProgress result = coordinator.ApplyOscProgress(stateCode, 73);

        Assert.Equal(expectedState, result.State);
        Assert.Equal(0, result.Progress);
        Assert.Equal(result, coordinator.Current);
    }

    [Fact]
    public void ClearResetsCurrentProgress()
    {
        var coordinator = new TerminalTaskbarProgressCoordinator();
        coordinator.ApplyOscProgress(2, 73);

        TerminalTaskbarProgress result = coordinator.Clear();

        Assert.Equal(new TerminalTaskbarProgress(TaskbarProgressState.None, 0), result);
        Assert.Equal(result, coordinator.Current);
    }
}
