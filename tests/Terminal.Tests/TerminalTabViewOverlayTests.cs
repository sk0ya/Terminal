using System.Windows;

using Terminal.Buffer;
using Terminal.Tabs;

namespace Terminal.Tests;

public sealed class TerminalTabViewOverlayTests
{
    [Fact]
    public void CalculateOverlayLayoutKeepsBlockCursorAlignedToTerminalCell()
    {
        (Rect proxyBounds, Rect cursorBounds) = TerminalTabView.CalculateOverlayLayout(
            cursorLeft: 95,
            cursorTop: 8,
            proxyWidth: 30,
            proxyHeight: 20,
            charWidth: 10,
            charHeight: 20,
            viewportBounds: new Rect(0, 0, 100, 40),
            cursorShape: TerminalCursorShape.Block);

        Assert.Equal(70, proxyBounds.Left, precision: 3);
        Assert.Equal(8, proxyBounds.Top, precision: 3);
        Assert.Equal(90, cursorBounds.Left, precision: 3);
        Assert.Equal(8, cursorBounds.Top, precision: 3);
        Assert.Equal(10, cursorBounds.Width, precision: 3);
        Assert.Equal(20, cursorBounds.Height, precision: 3);
    }

    [Fact]
    public void CalculateOverlayLayoutUsesProxyCaretWhenAvailable()
    {
        (Rect proxyBounds, Rect cursorBounds) = TerminalTabView.CalculateOverlayLayout(
            cursorLeft: 60,
            cursorTop: 8,
            proxyWidth: 30,
            proxyHeight: 20,
            charWidth: 10,
            charHeight: 20,
            viewportBounds: new Rect(0, 0, 200, 40),
            cursorShape: TerminalCursorShape.Block,
            proxyCaretBounds: new Rect(80, 8, 0, 20));

        Assert.Equal(60, proxyBounds.Left, precision: 3);
        Assert.Equal(80, cursorBounds.Left, precision: 3);
        Assert.Equal(8, cursorBounds.Top, precision: 3);
    }

    [Fact]
    public void ResolveRenderedCursorLineIgnoresScrollbackInAlternateScreen()
    {
        int lineIndex = TerminalTabView.ResolveRenderedCursorLine(
            cursorRow: 8,
            scrollbackLineCount: 40,
            isAlternateScreenActive: true,
            renderedLineCount: 20);

        Assert.Equal(8, lineIndex);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    public void ShouldUseProxyCaretRequiresActiveImeComposition(bool hasPendingProxyText, bool imeCompositionActive, bool expected)
    {
        var method = typeof(TerminalTabView).GetMethod(
            "ShouldUseProxyCaret",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);

        bool result = (bool)method!.Invoke(null, [hasPendingProxyText, imeCompositionActive])!;

        Assert.Equal(expected, result);
    }
}
