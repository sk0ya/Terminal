using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Threading;

using Terminal.Buffer;
using Terminal.Sessions;
using Terminal.Tabs;

namespace Terminal.Tests;

public sealed class TerminalTabViewAgentLifecycleTests
{
    [Fact]
    public void NoSessionAndPreCanceledCallsDoNotWrite()
    {
        StaTestRunner.Run(() =>
        {
            var view = new TerminalTabView();
            TerminalCommandResult noSession = AgentViewDriver.Await(view.RunCommandAsync("echo none"));
            Assert.False(noSession.Completed);
            Assert.Equal(-1, noSession.ExitCode);

            var session = new FakeAgentSession();
            AgentViewDriver.Attach(view, session, "cmd.exe /K");
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();

            Assert.ThrowsAny<OperationCanceledException>(() =>
                AgentViewDriver.Await(view.RunCommandAsync("echo canceled", canceled.Token)));
            Assert.Empty(session.Writes);
        });
    }

    [Fact]
    public void UnknownShellReturnsIncompleteWithoutWriting()
    {
        StaTestRunner.Run(() =>
        {
            var view = new TerminalTabView();
            var session = new FakeAgentSession();
            AgentViewDriver.Attach(view, session, "unknown-shell.exe");

            TerminalCommandResult result = AgentViewDriver.Await(view.RunCommandAsync("echo hello"));

            Assert.False(result.Completed);
            Assert.Empty(result.Output);
            Assert.Empty(session.Writes);
        });
    }

    [Fact]
    public void ShellIntegrationSendsExactCommandAndRequiresExecutedZoneBeforeDone()
    {
        StaTestRunner.Run(() =>
        {
            var view = new TerminalTabView();
            var session = new FakeAgentSession();
            AgentViewDriver.Attach(view, session, "pwsh.exe");
            AgentViewDriver.SetShellIntegration(view, active: true);

            Task<TerminalCommandResult> command = view.RunCommandAsync("Write-Output hello");
            Assert.Equal(["Write-Output hello\r"], session.Writes);
            AgentViewDriver.SendShellZone(view, ShellCommandZoneType.CommandDone, 1, 9);
            Assert.False(command.IsCompleted);

            int startLine = AgentViewDriver.CurrentAbsoluteLine(view);
            AgentViewDriver.SendShellZone(view, ShellCommandZoneType.CommandExecuted, startLine, null);
            view.FeedOutputForTests("hello\r\n");
            AgentViewDriver.SendShellZone(view, ShellCommandZoneType.CommandDone, startLine + 1, 3);
            TerminalCommandResult result = AgentViewDriver.Await(command);

            Assert.True(result.Completed);
            Assert.Equal(3, result.ExitCode);
            Assert.Contains("hello", result.Output);
        });
    }

    [Fact]
    public void SentinelWaitsForEndMarkerThenReturnsOutputAndExitCode()
    {
        StaTestRunner.Run(() =>
        {
            var view = new TerminalTabView();
            var session = new FakeAgentSession();
            AgentViewDriver.Attach(view, session, "cmd.exe /K");

            Task<TerminalCommandResult> command = view.RunCommandAsync("echo hello");
            Assert.Single(session.Writes);
            Assert.EndsWith("\r", session.Writes[0]);
            string marker = Regex.Match(session.Writes[0], @"__ASE_B_([0-9a-f]+)").Groups[1].Value;
            Assert.NotEmpty(marker);

            view.FeedOutputForTests($"__ASE_B_{marker}\r\npartial");
            AgentViewDriver.TryCompleteSentinel(view);
            Assert.False(command.IsCompleted);

            view.FeedOutputForTests($"\r\n__ASE_E_{marker}_7\r\n");
            AgentViewDriver.TryCompleteSentinel(view);
            TerminalCommandResult result = AgentViewDriver.Await(command);

            Assert.True(result.Completed);
            Assert.Equal(7, result.ExitCode);
            Assert.Equal("partial", result.Output);
        });
    }

    [Fact]
    public void ConcurrentCallsAreSerializedUntilActiveExecutionCompletes()
    {
        StaTestRunner.Run(() =>
        {
            var view = new TerminalTabView();
            var session = new FakeAgentSession();
            AgentViewDriver.Attach(view, session, "pwsh.exe");
            AgentViewDriver.SetShellIntegration(view, active: true);

            Task<TerminalCommandResult> first = view.RunCommandAsync("first");
            Task<TerminalCommandResult> second = view.RunCommandAsync("second");
            Assert.Equal(["first\r"], session.Writes);

            AgentViewDriver.Abort(view);
            _ = AgentViewDriver.Await(first);
            AgentViewDriver.PumpUntil(() => session.Writes.Count == 2);
            Assert.Equal("second\r", session.Writes[1]);

            AgentViewDriver.Abort(view);
            _ = AgentViewDriver.Await(second);
        });
    }

    [Fact]
    public void CancellationInterruptsAndReturnsPartialOutput()
    {
        StaTestRunner.Run(() =>
        {
            var view = new TerminalTabView();
            var session = new FakeAgentSession();
            AgentViewDriver.Attach(view, session, "cmd.exe /K");
            using var cancellation = new CancellationTokenSource();
            Task<TerminalCommandResult> command = view.RunCommandAsync("long-running", cancellation.Token);
            string marker = Regex.Match(session.Writes[0], @"__ASE_B_([0-9a-f]+)").Groups[1].Value;
            view.FeedOutputForTests($"__ASE_B_{marker}\r\npartial output");

            cancellation.Cancel();
            TerminalCommandResult result = AgentViewDriver.Await(command);

            Assert.False(result.Completed);
            Assert.Contains("partial output", result.Output);
            Assert.Equal(1, session.Writes.Count(write => write == "\u0003"));
        });
    }

    [Fact]
    public void AbortCompletesPartialResultAndStaleCallbacksDoNotAffectNextExecution()
    {
        StaTestRunner.Run(() =>
        {
            var view = new TerminalTabView();
            var session = new FakeAgentSession();
            AgentViewDriver.Attach(view, session, "cmd.exe /K");
            Task<TerminalCommandResult> first = view.RunCommandAsync("first");
            object firstExecution = AgentViewDriver.ActiveExecution(view)!;
            view.FeedOutputForTests("partial before replacement");

            AgentViewDriver.Unwire(view, session);
            TerminalCommandResult aborted = AgentViewDriver.Await(first);
            Assert.False(aborted.Completed);
            Assert.Contains("partial before replacement", aborted.Output);

            Task<TerminalCommandResult> second = view.RunCommandAsync("second");
            object secondExecution = AgentViewDriver.ActiveExecution(view)!;
            AgentViewDriver.Cancel(view, firstExecution);
            AgentViewDriver.Timeout(view, firstExecution);
            Assert.Same(secondExecution, AgentViewDriver.ActiveExecution(view));
            Assert.False(second.IsCompleted);
            Assert.DoesNotContain("\u0003", session.Writes);

            AgentViewDriver.Abort(view);
            _ = AgentViewDriver.Await(second);
        });
    }

    [Fact]
    public void TimeoutDoesNotInterruptAndBufferReplacementResetsShellIntegration()
    {
        StaTestRunner.Run(() =>
        {
            var view = new TerminalTabView();
            var session = new FakeAgentSession();
            AgentViewDriver.Attach(view, session, "cmd.exe /K");
            AgentViewDriver.SetShellIntegration(view, active: true);
            Task<TerminalCommandResult> command = view.RunCommandAsync("slow");
            object execution = AgentViewDriver.ActiveExecution(view)!;

            AgentViewDriver.Timeout(view, execution);
            TerminalCommandResult result = AgentViewDriver.Await(command);
            Assert.False(result.Completed);
            Assert.DoesNotContain("\u0003", session.Writes);

            Assert.True(view.IsShellIntegrationActive);
            AgentViewDriver.ReplaceBuffer(view);
            Assert.False(view.IsShellIntegrationActive);
        });
    }

    private sealed class FakeAgentSession : ITerminalSession
    {
        public List<string> Writes { get; } = [];
        public TerminalSessionCapabilities Capabilities { get; } = new(
            TerminalSessionKind.ConPty, SupportsResize: true, SupportsTerminalInput: true);
        public event EventHandler<string>? OutputReceived { add { } remove { } }
        public event EventHandler<int>? Exited { add { } remove { } }
        public void Start() { }
        public void Write(string input) => Writes.Add(input);
        public void Write(byte[] input) => Writes.Add(Convert.ToHexString(input));
        public void Resize(short columns, short rows) { }
        public bool TryForceUnlock(uint exitCode = 1) => true;
        public bool IsOutputStalled(TimeSpan initialOutputTimeout, TimeSpan idleOutputTimeout) => false;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static class AgentViewDriver
    {
        private static readonly BindingFlags InstanceFlags =
            BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly FieldInfo OrchestratorField = Field("_sessionOrchestrator");
        private static readonly FieldInfo LaunchField = Field("_launchState");
        private static readonly FieldInfo BufferField = Field("_terminalBuffer");
        private static readonly FieldInfo ActiveExecutionField = Field("_activeAgentCommand");
        private static readonly FieldInfo ShellIntegrationField = Field("_shellIntegrationObserved");

        public static void Attach(TerminalTabView view, FakeAgentSession session, string commandLine)
        {
            var orchestrator = (TerminalSessionOrchestrator)OrchestratorField.GetValue(view)!;
            TerminalSessionStartResult started = Await(orchestrator.StartAsync(
                () => Task.FromResult<ITerminalSession>(session),
                _ => { },
                _ => { },
                () => { }));
            Assert.True(started.Started);
            ((TerminalLaunchCoordinator)LaunchField.GetValue(view)!).Activate(commandLine, Environment.CurrentDirectory);
        }

        public static void SetShellIntegration(TerminalTabView view, bool active) =>
            ShellIntegrationField.SetValue(view, active);

        public static object? ActiveExecution(TerminalTabView view) => ActiveExecutionField.GetValue(view);

        public static int CurrentAbsoluteLine(TerminalTabView view)
        {
            var buffer = (AnsiTerminalBuffer)BufferField.GetValue(view)!;
            return buffer.ScrollbackLineCount + buffer.CursorRow;
        }

        public static void SendShellZone(
            TerminalTabView view,
            ShellCommandZoneType type,
            int absoluteLine,
            int? exitCode) =>
            Invoke(view, "OnAgentShellCommandZone", new ShellCommandZoneEventArgs(type, absoluteLine, exitCode));

        public static void TryCompleteSentinel(TerminalTabView view) => Invoke(view, "TryCompleteAgentSentinel");
        public static void Abort(TerminalTabView view) => Invoke(view, "AbortActiveAgentCommand");
        public static void Unwire(TerminalTabView view, ITerminalSession session) =>
            Invoke(view, "UnwireSessionEvents", session);
        public static void Cancel(TerminalTabView view, object execution) =>
            Invoke(view, "CancelAgentCommand", execution);
        public static void Timeout(TerminalTabView view, object execution) =>
            Invoke(view, "TimeoutAgentCommand", execution);
        public static void ReplaceBuffer(TerminalTabView view) =>
            Invoke(view, "ReplaceTerminalBuffer", new AnsiTerminalBuffer(80, 24));

        public static T Await<T>(Task<T> task)
        {
            PumpUntil(() => task.IsCompleted);
            return task.GetAwaiter().GetResult();
        }

        public static void PumpUntil(Func<bool> condition)
        {
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!condition())
            {
                if (DateTime.UtcNow >= deadline)
                {
                    throw new TimeoutException("Timed out pumping the STA dispatcher.");
                }

                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
                Thread.Yield();
            }
        }

        private static FieldInfo Field(string name) =>
            typeof(TerminalTabView).GetField(name, InstanceFlags)
            ?? throw new MissingFieldException(typeof(TerminalTabView).FullName, name);

        private static void Invoke(TerminalTabView view, string methodName, params object?[] arguments)
        {
            MethodInfo method = typeof(TerminalTabView).GetMethod(methodName, InstanceFlags)
                ?? throw new MissingMethodException(typeof(TerminalTabView).FullName, methodName);
            method.Invoke(view, arguments);
        }
    }
}
