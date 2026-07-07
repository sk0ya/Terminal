using System.Reflection;

using Terminal.Sessions;
using Terminal.Tabs;

namespace Terminal.Tests;

public sealed class TerminalTabViewSessionWatchdogTests
{
    [Fact]
    public void WatchdogTickPropagatesStallProbeFailureSynchronously()
    {
        StaTestRunner.Run(() =>
        {
            var view = new TerminalTabView();
            var session = new ThrowingStallSession();
            var orchestrator = (TerminalSessionOrchestrator)GetField(view, "_sessionOrchestrator");
            TerminalSessionStartResult started = orchestrator.StartAsync(
                () => Task.FromResult<ITerminalSession>(session),
                _ => { },
                _ => { },
                () => { }).GetAwaiter().GetResult();
            Assert.True(started.Started);

            TargetInvocationException error = Assert.Throws<TargetInvocationException>(() =>
                Invoke(view, "SessionWatchdog_Tick", null, EventArgs.Empty));

            Assert.Same(session.Error, error.InnerException);
            Assert.Equal(TimeSpan.FromSeconds(4), session.InitialOutputTimeout);
            Assert.Equal(TimeSpan.FromSeconds(20), session.IdleOutputTimeout);
        });
    }

    private static object GetField(TerminalTabView view, string name) =>
        typeof(TerminalTabView).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(view)!;

    private static void Invoke(TerminalTabView view, string methodName, params object?[] arguments) =>
        typeof(TerminalTabView).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(view, arguments);

    private sealed class ThrowingStallSession : ITerminalSession
    {
        public InvalidOperationException Error { get; } = new("stall probe failed");
        public TimeSpan InitialOutputTimeout { get; private set; }
        public TimeSpan IdleOutputTimeout { get; private set; }
        public TerminalSessionCapabilities Capabilities { get; } = new(
            TerminalSessionKind.ConPty, SupportsResize: true, SupportsTerminalInput: true);
        public event EventHandler<string>? OutputReceived { add { } remove { } }
        public event EventHandler<int>? Exited { add { } remove { } }
        public void Start() { }
        public bool IsOutputStalled(TimeSpan initialOutputTimeout, TimeSpan idleOutputTimeout)
        {
            InitialOutputTimeout = initialOutputTimeout;
            IdleOutputTimeout = idleOutputTimeout;
            throw Error;
        }
        public bool TryForceUnlock(uint exitCode = 1) => true;
        public void Resize(short columns, short rows) { }
        public void Write(string input) { }
        public void Write(byte[] input) { }
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
