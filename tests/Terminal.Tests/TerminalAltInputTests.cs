using System.Reflection;
using System.Windows;
using System.Windows.Input;

using Terminal.Input;
using Terminal.Sessions;
using Terminal.Tabs;

namespace Terminal.Tests;

/// <summary>
/// Alt+letter arrives as WM_SYSCHAR (empty Text, character in SystemText) and Alt+special keys as
/// Key.System. Keyboard focus sits on the hidden input proxy, which ignored both, so Alt+V (Claude
/// Code's image paste) and every other meta chord never reached the shell.
/// </summary>
public sealed class TerminalAltInputTests
{
    private const string Esc = "\u001b";

    [Fact]
    public void AltLetterOnTheInputProxyIsSentWithAnEscapePrefix()
    {
        StaTestRunner.Run(() =>
        {
            var (view, writes) = CreatePane();

            bool handled = RaiseSystemText(InputProxy(view), "v");

            Assert.True(handled);
            Assert.Equal([$"{Esc}v"], writes);
        });
    }

    [Fact]
    public void AltLetterOnTheOutputSurfaceIsSentWithAnEscapePrefix()
    {
        StaTestRunner.Run(() =>
        {
            var (view, writes) = CreatePane();

            RaiseSystemText((UIElement)view.FindName("TerminalOutput"), "b");

            Assert.Equal([$"{Esc}b"], writes);
        });
    }

    [Fact]
    public void AltLetterIsSentBareWhenTheApplicationTurnedAltSendsEscapeOff()
    {
        StaTestRunner.Run(() =>
        {
            var (view, writes) = CreatePane();
            view.FeedOutputForTests($"{Esc}[?1039l");

            RaiseSystemText(InputProxy(view), "v");

            Assert.Equal(["v"], writes);
        });
    }

    [Theory]
    [InlineData(Key.Left, $"{Esc}[1;3D")]
    [InlineData(Key.Enter, $"{Esc}\r")]
    [InlineData(Key.Back, $"{Esc}\u007f")]
    public void AltSpecialKeysAreEncodedOnceResolvedFromKeySystem(Key key, string expected)
    {
        Assert.Equal(expected, TerminalKeyChordTranslator.TranslateSpecialKey(key, ModifierKeys.Alt, applicationCursorKeys: false, 0));
    }

    [Fact]
    public void AltF4StaysWithWindowsToCloseTheWindow()
    {
        Assert.Null(TerminalKeyChordTranslator.TranslateSpecialKey(Key.F4, ModifierKeys.Alt, applicationCursorKeys: false, 0));
        Assert.Null(TerminalKeyChordTranslator.TranslateSpecialKey(Key.F4, ModifierKeys.Alt, applicationCursorKeys: false, 0, kittyKeyboardFlags: 0x08));
    }

    [Fact]
    public void ShiftedOrControlledF4StillReachesTheShell()
    {
        Assert.NotNull(TerminalKeyChordTranslator.TranslateSpecialKey(Key.F4, ModifierKeys.Alt | ModifierKeys.Shift, applicationCursorKeys: false, 0));
        Assert.NotNull(TerminalKeyChordTranslator.TranslateSpecialKey(Key.F4, ModifierKeys.None, applicationCursorKeys: false, 0));
    }

    private static UIElement InputProxy(TerminalTabView view) => (UIElement)view.FindName("TerminalInputProxy");

    /// <summary>Raises PreviewTextInput the way WM_SYSCHAR does: Text empty, the character in SystemText.</summary>
    private static bool RaiseSystemText(UIElement target, string character)
    {
        var composition = new TextComposition(InputManager.Current, target, character);
        typeof(TextComposition)
            .GetMethod("MakeSystem", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(composition, null);
        var args = new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
        {
            RoutedEvent = TextCompositionManager.PreviewTextInputEvent
        };
        Assert.Equal(string.Empty, args.Text);
        Assert.Equal(character, args.SystemText);

        target.RaiseEvent(args);
        return args.Handled;
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
