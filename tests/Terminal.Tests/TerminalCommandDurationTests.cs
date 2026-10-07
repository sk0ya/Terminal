using Terminal.Buffer;
using Terminal.Tabs;

namespace Terminal.Tests;

public sealed class TerminalCommandDurationTests
{
    private static readonly DateTime T0 = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void DoneRecordsExitCodeAndTimeFromExecutedToDone()
    {
        var coordinator = new TerminalCommandNavigationCoordinator();
        coordinator.Observe(ShellCommandZoneType.PromptStart, 3, null, T0);
        coordinator.Observe(ShellCommandZoneType.CommandStart, 3, null, T0);
        coordinator.Observe(ShellCommandZoneType.CommandExecuted, 3, null, T0.AddSeconds(1));
        coordinator.Observe(ShellCommandZoneType.CommandDone, 9, 2, T0.AddSeconds(13.5));

        TerminalCommandMark mark = Assert.IsType<TerminalCommandMark>(coordinator.LastCommand);
        Assert.True(mark.Done);
        Assert.Equal(2, mark.ExitCode);
        Assert.Equal(TimeSpan.FromSeconds(12.5), mark.Duration);
    }

    [Fact]
    public void RepeatedDoneKeepsTheFirstOutcome()
    {
        var coordinator = new TerminalCommandNavigationCoordinator();
        coordinator.Observe(ShellCommandZoneType.PromptStart, 0, null, T0);
        coordinator.Observe(ShellCommandZoneType.CommandExecuted, 0, null, T0);
        coordinator.Observe(ShellCommandZoneType.CommandDone, 1, 0, T0.AddSeconds(2));
        coordinator.Observe(ShellCommandZoneType.CommandDone, 1, 5, T0.AddSeconds(60));

        TerminalCommandMark mark = Assert.IsType<TerminalCommandMark>(coordinator.LastCommand);
        Assert.Equal(0, mark.ExitCode);
        Assert.Equal(TimeSpan.FromSeconds(2), mark.Duration);
    }

    [Fact]
    public void DoneWithoutExecutedHasNoDuration()
    {
        var coordinator = new TerminalCommandNavigationCoordinator();
        coordinator.Observe(ShellCommandZoneType.PromptStart, 0, null, T0);
        coordinator.Observe(ShellCommandZoneType.CommandDone, 0, 0, T0.AddSeconds(2));

        TerminalCommandMark mark = Assert.IsType<TerminalCommandMark>(coordinator.LastCommand);
        Assert.True(mark.Done);
        Assert.Null(mark.Duration);
    }

    [Fact]
    public void FindOwnerReturnsTheCommandWhosePromptStartedLastAboveTheLine()
    {
        var coordinator = new TerminalCommandNavigationCoordinator();
        coordinator.Observe(ShellCommandZoneType.PromptStart, 0, null, T0);
        coordinator.Observe(ShellCommandZoneType.PromptStart, 10, null, T0);

        Assert.Equal(0, coordinator.FindOwner(9)?.PromptLine);
        Assert.Equal(10, coordinator.FindOwner(10)?.PromptLine);
        Assert.Null(new TerminalCommandNavigationCoordinator().FindOwner(3));
    }

    [Theory]
    [InlineData(0.25, "250ms")]
    [InlineData(1.0, "1.0s")]
    [InlineData(12.34, "12.3s")]
    [InlineData(245, "4m 05s")]
    [InlineData(3720, "1h 02m")]
    public void FormatCommandDurationIsCompact(double seconds, string expected)
    {
        Assert.Equal(expected, TerminalTabView.FormatCommandDuration(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void FinishedStatusReportsFailuresAlwaysAndSuccessOnlyWhenSlow()
    {
        Assert.Equal("Command exited with code 1.", TerminalTabView.FormatCommandFinishedStatus(1, null));
        Assert.Equal(
            "Command exited with code 1 after 250ms.",
            TerminalTabView.FormatCommandFinishedStatus(1, TimeSpan.FromMilliseconds(250)));
        Assert.Null(TerminalTabView.FormatCommandFinishedStatus(0, TimeSpan.FromMilliseconds(250)));
        Assert.Null(TerminalTabView.FormatCommandFinishedStatus(null, null));
        Assert.Equal("Command finished in 3.0s.", TerminalTabView.FormatCommandFinishedStatus(0, TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void CommandMetaShowsOutcomeAndDuration()
    {
        Assert.Equal(string.Empty, TerminalTabView.FormatCommandMeta(null));
        Assert.Equal("…", TerminalTabView.FormatCommandMeta(new TerminalCommandMark(0, 0, Executed: true)));
        Assert.Equal(
            "✓ · 1.5s",
            TerminalTabView.FormatCommandMeta(new TerminalCommandMark(0, 0, true, T0, Done: true, ExitCode: 0, Duration: TimeSpan.FromSeconds(1.5))));
        Assert.Equal(
            "✗ 2",
            TerminalTabView.FormatCommandMeta(new TerminalCommandMark(0, 0, true, Done: true, ExitCode: 2)));
    }
}
