using System.Text;
using Terminal.Buffer;
using Terminal.Tabs;

namespace Terminal.Tests;

public sealed class TerminalMouseCoordinatorTests
{
    private readonly TerminalMouseCoordinator _coordinator = new();

    [Fact]
    public void InputAndTrackingGateMouseReports()
    {
        Assert.False(_coordinator.ResolveButton(State(supportsInput: false), TerminalMouseButton.Left, true, 1, 1).Handled);
        Assert.False(_coordinator.ResolveButton(State(tracking: TerminalMouseTrackingMode.Off), TerminalMouseButton.Left, true, 1, 1).Handled);
        Assert.False(_coordinator.ResolveButton(State(tracking: TerminalMouseTrackingMode.X10), TerminalMouseButton.Left, false, 1, 1).Handled);
        Assert.True(_coordinator.ResolveButton(State(), TerminalMouseButton.Unsupported, false, 1, 1).Handled);
    }

    [Fact]
    public void ButtonReportsPreserveButtonModifierAndReleaseCodes()
    {
        TerminalMouseState state = State(encoding: TerminalMouseEncoding.Sgr, modifiers: TerminalMouseModifiers.Shift | TerminalMouseModifiers.Control);

        Assert.Equal("\u001b[<20;4;5M", Text(_coordinator.ResolveButton(state, TerminalMouseButton.Left, true, 4, 5)));
        Assert.Equal("\u001b[<23;4;5m", Text(_coordinator.ResolveButton(state, TerminalMouseButton.Right, false, 4, 5)));
    }

    [Fact]
    public void MoveRequiresButtonOnlyInButtonEventMode()
    {
        Assert.False(_coordinator.ResolveMove(State(tracking: TerminalMouseTrackingMode.ButtonEvent), TerminalMouseButton.None, 2, 3).Handled);
        Assert.Equal("\u001b[<35;2;3M", Text(_coordinator.ResolveMove(State(encoding: TerminalMouseEncoding.Sgr), TerminalMouseButton.None, 2, 3)));
        Assert.Equal("\u001b[<32;2;3M", Text(_coordinator.ResolveMove(State(encoding: TerminalMouseEncoding.Sgr), TerminalMouseButton.Left, 2, 3)));
    }

    [Theory]
    [InlineData(120, 64)]
    [InlineData(-120, 65)]
    public void WheelUsesXtermWheelCodes(int delta, int code)
    {
        Assert.Equal($"\u001b[<{code};8;9M", Text(_coordinator.ResolveWheel(State(encoding: TerminalMouseEncoding.Sgr), delta, 8, 9, 120)));
    }

    [Fact]
    public void AlternateScrollRequiresModesAndRepeatsCursorSequence()
    {
        TerminalMouseState disabled = State(tracking: TerminalMouseTrackingMode.Off);
        Assert.False(_coordinator.ResolveWheel(disabled, 120, 1, 1, 120).Handled);

        TerminalMouseState enabled = disabled with
        {
            AlternateScrollEnabled = true,
            IsAlternateScreenActive = true,
            AlternateScrollUpSequence = "\u001b[A",
            AlternateScrollDownSequence = "\u001b[B"
        };
        Assert.Equal("\u001b[A\u001b[A", Text(_coordinator.ResolveWheel(enabled, 240, 1, 1, 120)));
        Assert.Equal("\u001bOB", Text(_coordinator.ResolveWheel(enabled with { AlternateScrollDownSequence = "\u001bOB" }, -120, 1, 1, 120)));
    }

    [Fact]
    public void CaptureDecisionsFollowTrackingAndPressedButtons()
    {
        Assert.True(_coordinator.ShouldCapture(State()));
        Assert.False(_coordinator.ShouldCapture(State(supportsInput: false)));
        Assert.False(_coordinator.ShouldCapture(State(tracking: TerminalMouseTrackingMode.Off)));
        Assert.False(_coordinator.ShouldReleaseCapture(force: false, hasPressedButton: true));
        Assert.True(_coordinator.ShouldReleaseCapture(force: true, hasPressedButton: true));
        Assert.True(_coordinator.ShouldReleaseCapture(force: false, hasPressedButton: false));
    }

    [Theory]
    [InlineData((int)TerminalMouseEncoding.Default)]
    [InlineData((int)TerminalMouseEncoding.Utf8)]
    [InlineData((int)TerminalMouseEncoding.Sgr)]
    [InlineData((int)TerminalMouseEncoding.Urxvt)]
    public void MouseReportsRemainBytePayloadsAcrossEncodings(int encodingValue)
    {
        var encoding = (TerminalMouseEncoding)encodingValue;
        TerminalMouseAction action = _coordinator.ResolveButton(State(encoding: encoding), TerminalMouseButton.Left, true, 10, 20);

        Assert.NotNull(action.BytePayload);
        Assert.Null(action.TextPayload);
    }

    [Fact]
    public void AlternateScrollRemainsTextPayload()
    {
        TerminalMouseState state = State(tracking: TerminalMouseTrackingMode.Off) with
        {
            AlternateScrollEnabled = true,
            IsAlternateScreenActive = true,
            AlternateScrollUpSequence = "\u001b[A"
        };

        TerminalMouseAction action = _coordinator.ResolveWheel(state, 120, 1, 1, 120);
        Assert.Null(action.BytePayload);
        Assert.Equal("\u001b[A", action.TextPayload);
    }

    private static string Text(TerminalMouseAction action) =>
        action.TextPayload ?? Encoding.UTF8.GetString(action.BytePayload!);

    private static TerminalMouseState State(
        bool supportsInput = true,
        TerminalMouseTrackingMode tracking = TerminalMouseTrackingMode.AnyEvent,
        TerminalMouseEncoding encoding = TerminalMouseEncoding.Sgr,
        TerminalMouseModifiers modifiers = TerminalMouseModifiers.None) =>
        new(supportsInput, tracking, encoding, false, false, null, null, modifiers);
}
