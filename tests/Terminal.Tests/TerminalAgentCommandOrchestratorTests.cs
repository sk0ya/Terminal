using Terminal.Buffer;
using Terminal.Tabs;

namespace Terminal.Tests;

public sealed class TerminalAgentCommandOrchestratorTests
{
    [Fact]
    public async Task TimeoutSchedulerCompletesPartialResultWithoutInterrupt()
    {
        var fixture = new Fixture();
        fixture.CapturedOutput = "partial";

        Task<TerminalCommandResult> command = fixture.Orchestrator.RunAsync("slow", CancellationToken.None);
        Assert.Single(fixture.Writes);
        Assert.Equal(TimeSpan.FromMinutes(10), fixture.Timeout.LastDelay);

        fixture.Timeout.Fire();
        TerminalCommandResult result = await command;

        Assert.False(result.Completed);
        Assert.Equal("partial", result.Output);
        Assert.Equal(0, fixture.InterruptCount);
        Assert.Equal(1, fixture.DispatchCount);
        Assert.True(fixture.Timeout.IsDisposed);
    }

    [Fact]
    public async Task CancellationDispatchesInterruptAndDisposesTimeout()
    {
        var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        Task<TerminalCommandResult> command = fixture.Orchestrator.RunAsync("slow", cancellation.Token);

        cancellation.Cancel();
        TerminalCommandResult result = await command;

        Assert.False(result.Completed);
        Assert.Equal(1, fixture.InterruptCount);
        Assert.Equal(1, fixture.DispatchCount);
        Assert.True(fixture.Timeout.IsDisposed);
    }

    [Fact]
    public async Task GateDoesNotSendSecondCommandUntilFirstCompletes()
    {
        var fixture = new Fixture(shellIntegration: true);
        Task<TerminalCommandResult> first = fixture.Orchestrator.RunAsync("first", CancellationToken.None);
        Task<TerminalCommandResult> second = fixture.Orchestrator.RunAsync("second", CancellationToken.None);

        Assert.Equal(["first\r"], fixture.Writes);
        fixture.Coordinator.Abort(_ => string.Empty);
        await first;
        await WaitUntilAsync(() => fixture.Writes.Count == 2);
        Assert.Equal("second\r", fixture.Writes[1]);

        fixture.Coordinator.Abort(_ => string.Empty);
        await second;
    }

    [Fact]
    public async Task SendFailureAbandonsExecutionAndDisposesRegistrations()
    {
        var fixture = new Fixture { SendSucceeds = false };

        TerminalCommandResult result = await fixture.Orchestrator.RunAsync("command", CancellationToken.None);

        Assert.False(result.Completed);
        Assert.Null(fixture.Coordinator.Current);
        Assert.True(fixture.Timeout.IsDisposed);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(1, timeout.Token);
        }
    }

    private sealed class Fixture
    {
        public Fixture(bool shellIntegration = false)
        {
            if (shellIntegration)
            {
                Coordinator.OnShellZone(
                    new ShellCommandZoneEventArgs(ShellCommandZoneType.PromptStart, 0, null),
                    (_, _) => string.Empty);
            }

            Orchestrator = new(
                Coordinator,
                new TerminalAgentCommandHost(
                    () => true,
                    () => "cmd.exe /K",
                    () => 4,
                    input => { Writes.Add(input); return SendSucceeds; },
                    () => InterruptCount++,
                    _ => CapturedOutput,
                    action => { DispatchCount++; action(); }),
                Timeout,
                TimeSpan.FromMinutes(10));
        }

        public TerminalAgentCommandCoordinator Coordinator { get; } = new();
        public FakeTimeoutScheduler Timeout { get; } = new();
        public TerminalAgentCommandOrchestrator Orchestrator { get; }
        public List<string> Writes { get; } = [];
        public bool SendSucceeds { get; set; } = true;
        public string CapturedOutput { get; set; } = string.Empty;
        public int InterruptCount { get; private set; }
        public int DispatchCount { get; private set; }
    }

    private sealed class FakeTimeoutScheduler : ITerminalAgentTimeoutScheduler
    {
        private Action? _callback;

        public TimeSpan LastDelay { get; private set; }
        public bool IsDisposed { get; private set; }

        public IDisposable Schedule(TimeSpan delay, Action callback)
        {
            LastDelay = delay;
            _callback = callback;
            IsDisposed = false;
            return new Registration(() => IsDisposed = true);
        }

        public void Fire() => (_callback ?? throw new InvalidOperationException("No timeout scheduled."))();

        private sealed class Registration(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }
}
