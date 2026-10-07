using System.Reflection;

using Terminal.Sessions;
using Terminal.Tabs;

namespace Terminal.Tests;

public sealed class TerminalUserInputSentTests
{
    [Fact]
    public void UserInputIsReportedButProgrammaticInputIsNot()
    {
        StaTestRunner.Run(() =>
        {
            var view = new TerminalTabView();
            var session = new RecordingSession();
            Attach(view, session);
            var raised = new List<string>();
            view.UserInputSent += (_, e) => raised.Add(e.Text);

            Assert.True(SendUserInput(view, "ls\r"));
            Assert.True(view.SendTerminalInput("mirrored\r"));

            Assert.Equal(["ls\r"], raised);
            Assert.Equal(["ls\r", "mirrored\r"], session.Writes);
        });
    }

    [Fact]
    public void BroadcastIndicatorToggles()
    {
        StaTestRunner.Run(() =>
        {
            var view = new TerminalTabView("cmd.exe", Environment.CurrentDirectory);
            Assert.False(view.IsInputBroadcastIndicatorVisible);
            view.IsInputBroadcastIndicatorVisible = true;
            Assert.True(view.IsInputBroadcastIndicatorVisible);
        });
    }

    private static bool SendUserInput(TerminalTabView view, string text) =>
        (bool)typeof(TerminalTabView)
            .GetMethod("SendUserInput", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(view, [text, null])!;

    private static void Attach(TerminalTabView view, ITerminalSession session)
    {
        var orchestrator = (TerminalSessionOrchestrator)typeof(TerminalTabView)
            .GetField("_sessionOrchestrator", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(view)!;
        Task<TerminalSessionStartResult> start = orchestrator.StartAsync(
            () => Task.FromResult(session),
            _ => { },
            _ => { },
            () => { });
        Assert.True(start.GetAwaiter().GetResult().Started);
    }

    private sealed class RecordingSession : ITerminalSession
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
}
