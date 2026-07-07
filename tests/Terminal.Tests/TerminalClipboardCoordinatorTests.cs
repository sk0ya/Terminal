using Terminal.Tabs;

namespace Terminal.Tests;

public sealed class TerminalClipboardCoordinatorTests
{
    private readonly TerminalClipboardCoordinator _coordinator = new();

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void CanPasteRequiresSessionAndClipboardText(bool hasSession, bool containsText, bool expected)
    {
        Assert.Equal(expected, _coordinator.CanPaste(hasSession, containsText));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void PasteSessionAndTextGatesArePreserved(bool hasSession, bool containsText)
    {
        TerminalPasteAction action = _coordinator.ResolvePaste(hasSession, containsText, "text", false, false);
        Assert.Equal(TerminalPasteActionKind.Ignore, action.Kind);
    }

    [Fact]
    public void PlainSingleLinePasteSendsTextUnchanged()
    {
        Assert.Equal(
            new TerminalPasteAction(TerminalPasteActionKind.Send, "plain text"),
            _coordinator.ResolvePaste(true, true, "plain text", false, false));
    }

    [Theory]
    [InlineData("one\ntwo")]
    [InlineData("one\rtwo")]
    [InlineData("one\r\ntwo")]
    public void PlainMultilinePasteRequiresApproval(string text)
    {
        Assert.Equal(
            TerminalPasteActionKind.ConfirmMultiline,
            _coordinator.ResolvePaste(true, true, text, false, false).Kind);
        Assert.Equal(
            new TerminalPasteAction(TerminalPasteActionKind.Send, text),
            _coordinator.ResolvePaste(true, true, text, false, true));
    }

    [Fact]
    public void BracketedPasteFramesTextWithoutConfirmation()
    {
        TerminalPasteAction action = _coordinator.ResolvePaste(true, true, "one\r\ntwo", true, false);
        Assert.Equal(new TerminalPasteAction(TerminalPasteActionKind.Send, "\u001b[200~one\r\ntwo\u001b[201~"), action);
    }

    [Theory]
    [InlineData(null, "c")]
    [InlineData("", "c")]
    [InlineData("   ", "c")]
    [InlineData("  s0  ", "s0")]
    public void Osc52ResponseNormalizesTargetAndUsesExactFraming(string? target, string expectedTarget)
    {
        Assert.Equal($"\u001b]52;{expectedTarget};44GC44GE44GG\u0007", _coordinator.BuildOsc52Response(target, "あいう"));
    }

    [Fact]
    public void Osc52ResponseUsesUtf8Base64ForUnicodeAndEmptyClipboard()
    {
        Assert.Equal("\u001b]52;c;8J+YgA==\u0007", _coordinator.BuildOsc52Response("c", "😀"));
        Assert.Equal("\u001b]52;c;\u0007", _coordinator.BuildOsc52Response("c", string.Empty));
    }
}
