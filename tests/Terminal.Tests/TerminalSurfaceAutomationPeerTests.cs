using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using Terminal.Buffer;
using Terminal.Rendering;

namespace Terminal.Tests;

public sealed class TerminalSurfaceAutomationPeerTests
{
    [Fact]
    public void SurfaceExposesReadOnlyTextAndValuePatterns()
    {
        RunSta(() =>
        {
            var surface = new TerminalSurfaceControl();
            var buffer = new AnsiTerminalBuffer(20, 2);
            buffer.Process("hello\r\nworld");
            surface.UpdateSnapshot(buffer.CreateRenderSnapshot(false));
            AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(surface)!;
            var text = Assert.IsAssignableFrom<ITextProvider>(peer.GetPattern(PatternInterface.Text));
            var value = Assert.IsAssignableFrom<IValueProvider>(peer.GetPattern(PatternInterface.Value));
            Assert.Contains("hello", text.DocumentRange.GetText(-1));
            Assert.Contains("world", value.Value);
            Assert.True(value.IsReadOnly);
            Assert.Equal(AutomationControlType.Document, peer.GetAutomationControlType());
        });
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
