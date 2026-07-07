using Terminal.Buffer;
using Terminal.Tabs;

namespace Terminal.Tests;

public sealed class TerminalAgentCommandCoordinatorTests
{
    [Fact]
    public void BeginSnapshotsShellIntegrationModeAndSessionResetDoesNotChangeActiveExecution()
    {
        var coordinator = new TerminalAgentCommandCoordinator();
        TerminalAgentCommandExecution fallback = coordinator.Begin("first", "marker", 12);

        coordinator.OnShellZone(
            new ShellCommandZoneEventArgs(ShellCommandZoneType.PromptStart, 0, null),
            (_, _) => string.Empty);
        TerminalAgentCommandExecution integrated = coordinator.Begin("second", "ignored", 99);
        coordinator.ResetSession();

        Assert.False(fallback.UseShellIntegration);
        Assert.Equal("marker", fallback.MarkerId);
        Assert.Equal(12, fallback.OutputStartLine);
        Assert.True(integrated.UseShellIntegration);
        Assert.Empty(integrated.MarkerId);
        Assert.Equal(-1, integrated.OutputStartLine);
        Assert.True(integrated.UseShellIntegration);
        Assert.False(coordinator.ShellIntegrationObserved);
    }

    [Fact]
    public async Task ShellDoneRequiresExecutedZoneAndLatestExecutedZoneWins()
    {
        var coordinator = IntegratedCoordinator();
        TerminalAgentCommandExecution execution = coordinator.Begin("command", string.Empty, -1);
        var captures = new List<(int Start, int End)>();

        Assert.False(coordinator.OnShellZone(
            new ShellCommandZoneEventArgs(ShellCommandZoneType.CommandDone, 20, 9), Capture));
        Assert.False(execution.Completion.Task.IsCompleted);
        Assert.False(coordinator.OnShellZone(
            new ShellCommandZoneEventArgs(ShellCommandZoneType.CommandExecuted, 4, null), Capture));
        Assert.False(coordinator.OnShellZone(
            new ShellCommandZoneEventArgs(ShellCommandZoneType.CommandExecuted, 7, null), Capture));
        Assert.True(coordinator.OnShellZone(
            new ShellCommandZoneEventArgs(ShellCommandZoneType.CommandDone, 10, null), Capture));
        Assert.False(coordinator.OnShellZone(
            new ShellCommandZoneEventArgs(ShellCommandZoneType.CommandDone, 12, 4), Capture));

        TerminalCommandResult result = await execution.Completion.Task;
        Assert.Equal([(7, 10)], captures);
        Assert.Equal("range:7-10", result.Output);
        Assert.Equal(-1, result.ExitCode);
        Assert.True(result.Completed);

        string Capture(int start, int end)
        {
            captures.Add((start, end));
            return $"range:{start}-{end}";
        }
    }

    [Fact]
    public async Task SentinelCompletionWinsAndLaterSignalsAreStale()
    {
        var coordinator = new TerminalAgentCommandCoordinator();
        TerminalAgentCommandExecution execution = coordinator.Begin("command", "id", 3);

        Assert.False(coordinator.TryCompleteSentinel("__ASE_B_id\r\npartial"));
        Assert.True(coordinator.TryCompleteSentinel("__ASE_B_id\r\nout\r\n__ASE_E_id_5\r\n"));
        Assert.False(coordinator.Cancel(execution, () => throw new Xunit.Sdk.XunitException("stale interrupt"), _ => "stale"));

        TerminalCommandResult result = await execution.Completion.Task;
        Assert.Equal("out", result.Output);
        Assert.Equal(5, result.ExitCode);
        Assert.True(result.Completed);
        Assert.Null(coordinator.Current);
    }

    [Fact]
    public async Task CancelIsFirstWinnerAndInterruptsBeforeCapturingPartialOutput()
    {
        var coordinator = new TerminalAgentCommandCoordinator();
        TerminalAgentCommandExecution execution = coordinator.Begin("command", "id", 8);
        var order = new List<string>();

        Assert.True(coordinator.Cancel(
            execution,
            () => order.Add("interrupt"),
            start => { order.Add($"capture:{start}"); return "partial"; }));
        Assert.False(coordinator.Timeout(execution, _ => "late"));
        Assert.False(coordinator.Abort(_ => "late"));

        TerminalCommandResult result = await execution.Completion.Task;
        Assert.Equal(["interrupt", "capture:8"], order);
        Assert.Equal("partial", result.Output);
        Assert.False(result.Completed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TimeoutAndAbortCompleteWithoutInterrupt(bool timeout)
    {
        var coordinator = new TerminalAgentCommandCoordinator();
        TerminalAgentCommandExecution execution = coordinator.Begin("command", "id", 2);
        int captureCount = 0;

        bool completed = timeout
            ? coordinator.Timeout(execution, start => { captureCount++; return $"from:{start}"; })
            : coordinator.Abort(start => { captureCount++; return $"from:{start}"; });

        Assert.True(completed);
        Assert.False(coordinator.Timeout(execution, _ => throw new InvalidOperationException("stale timeout")));
        Assert.False(coordinator.Abort(_ => throw new InvalidOperationException("stale abort")));
        Assert.False(coordinator.Cancel(
            execution,
            () => throw new InvalidOperationException("stale interrupt"),
            _ => throw new InvalidOperationException("stale capture")));
        TerminalCommandResult result = await execution.Completion.Task;
        Assert.Equal(1, captureCount);
        Assert.Equal("from:2", result.Output);
        Assert.False(result.Completed);
    }

    [Fact]
    public void StaleExecutionCannotAffectReplacement()
    {
        var coordinator = new TerminalAgentCommandCoordinator();
        TerminalAgentCommandExecution stale = coordinator.Begin("old", "old", 1);
        TerminalAgentCommandExecution current = coordinator.Begin("new", "new", 2);
        int sideEffects = 0;

        Assert.False(coordinator.Cancel(stale, () => sideEffects++, _ => { sideEffects++; return ""; }));
        Assert.False(coordinator.Timeout(stale, _ => { sideEffects++; return ""; }));

        Assert.Equal(0, sideEffects);
        Assert.Same(current, coordinator.Current);
        Assert.False(current.Completion.Task.IsCompleted);
    }

    [Fact]
    public void AbandonClearsOnlyTheMatchingCurrentExecution()
    {
        var coordinator = new TerminalAgentCommandCoordinator();
        TerminalAgentCommandExecution stale = coordinator.Begin("old", "old", 1);
        TerminalAgentCommandExecution current = coordinator.Begin("new", "new", 2);

        Assert.False(coordinator.Abandon(stale));
        Assert.Same(current, coordinator.Current);
        Assert.True(coordinator.Abandon(current));
        Assert.Null(coordinator.Current);
        Assert.False(stale.Completion.Task.IsCompleted);
        Assert.False(current.Completion.Task.IsCompleted);
    }

    private static TerminalAgentCommandCoordinator IntegratedCoordinator()
    {
        var coordinator = new TerminalAgentCommandCoordinator();
        coordinator.OnShellZone(
            new ShellCommandZoneEventArgs(ShellCommandZoneType.PromptStart, 0, null),
            (_, _) => string.Empty);
        return coordinator;
    }
}
