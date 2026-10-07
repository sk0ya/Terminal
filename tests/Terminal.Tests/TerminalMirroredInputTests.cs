using System.Reflection;
using System.Windows.Input;

using Terminal.Sessions;
using Terminal.Tabs;

namespace Terminal.Tests;

/// <summary>Broadcast input is re-encoded for the receiving pane's own input modes.</summary>
public sealed class TerminalMirroredInputTests
{
    private const string Esc = "\u001b";

    [Fact]
    public void ArrowKeysFollowTheReceivingPanesCursorKeyMode()
    {
        StaTestRunner.Run(() =>
        {
            var (normal, normalWrites) = CreatePane();
            var (vim, vimWrites) = CreatePane();
            vim.FeedOutputForTests($"{Esc}[?1h"); // DECCKM: application cursor keys

            var up = new TerminalUserInputEventArgs($"{Esc}[A", TerminalInputOrigin.FromKey(Key.Up, ModifierKeys.None));
            Assert.True(normal.SendMirroredInput(up));
            Assert.True(vim.SendMirroredInput(up));

            Assert.Equal([$"{Esc}[A"], normalWrites);
            Assert.Equal([$"{Esc}OA"], vimWrites);
        });
    }

    [Fact]
    public void PastesAreBracketedOnlyWhereTheReceivingPaneAskedForIt()
    {
        StaTestRunner.Run(() =>
        {
            var (plain, plainWrites) = CreatePane();
            var (bracketed, bracketedWrites) = CreatePane();
            bracketed.FeedOutputForTests($"{Esc}[?2004h");

            var paste = new TerminalUserInputEventArgs($"{Esc}[200~ls{Esc}[201~", TerminalInputOrigin.FromPaste("ls"));
            plain.SendMirroredInput(paste);
            bracketed.SendMirroredInput(paste);

            Assert.Equal(["ls"], plainWrites);
            Assert.Equal([$"{Esc}[200~ls{Esc}[201~"], bracketedWrites);
        });
    }

    [Fact]
    public void TypedTextIsSentAsIs()
    {
        StaTestRunner.Run(() =>
        {
            var (pane, writes) = CreatePane();
            pane.SendMirroredInput(new TerminalUserInputEventArgs("abc"));
            Assert.Equal(["abc"], writes);
        });
    }

    private static (TerminalTabView View, List<string> Writes) CreatePane()
    {
        var view = new TerminalTabView("cmd.exe", Environment.CurrentDirectory);
        var session = new RecordingSession();
        var orchestrator = (TerminalSessionOrchestrator)typeof(TerminalTabView)
            .GetField("_sessionOrchestrator", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(view)!;
        Assert.True(orchestrator.StartAsync(() => Task.FromResult<ITerminalSession>(session), _ => { }, _ => { }, () => { })
            .GetAwaiter().GetResult().Started);
        return (view, session.Writes);
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
