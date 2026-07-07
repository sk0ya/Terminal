using Terminal.Buffer;
using Terminal.Input;

namespace Terminal.Tabs;

internal enum TerminalMouseButton { None = 3, Left = 0, Middle = 1, Right = 2, Unsupported = -1 }
[Flags]
internal enum TerminalMouseModifiers { None = 0, Shift = 1, Control = 2, Alt = 4 }

internal sealed record TerminalMouseState(
    bool SupportsInput,
    TerminalMouseTrackingMode TrackingMode,
    TerminalMouseEncoding Encoding,
    bool AlternateScrollEnabled,
    bool IsAlternateScreenActive,
    string? AlternateScrollUpSequence,
    string? AlternateScrollDownSequence,
    TerminalMouseModifiers Modifiers);

internal sealed record TerminalMouseAction(bool Handled, byte[]? BytePayload = null, string? TextPayload = null);

internal sealed class TerminalMouseCoordinator
{
    public TerminalMouseAction ResolveButton(
        TerminalMouseState state, TerminalMouseButton button, bool pressed, int column, int row)
    {
        if (!CanReport(state) || pressed && button == TerminalMouseButton.Unsupported ||
            state.TrackingMode == TerminalMouseTrackingMode.X10 && !pressed)
        {
            return Unhandled();
        }

        int code = pressed ? (int)button : 3;
        return Encode(state, code, column, row, sgrRelease: !pressed);
    }

    public TerminalMouseAction ResolveMove(
        TerminalMouseState state, TerminalMouseButton pressedButton, int column, int row)
    {
        if (!CanReport(state) || state.TrackingMode == TerminalMouseTrackingMode.X10 ||
            state.TrackingMode == TerminalMouseTrackingMode.ButtonEvent && pressedButton == TerminalMouseButton.None)
        {
            return Unhandled();
        }

        int code = (int)pressedButton + 32;
        return Encode(state, code, column, row, sgrRelease: false);
    }

    public TerminalMouseAction ResolveWheel(
        TerminalMouseState state, int delta, int column, int row, int wheelDelta)
    {
        if (!state.SupportsInput)
        {
            return Unhandled();
        }

        if (state.TrackingMode != TerminalMouseTrackingMode.Off)
        {
            return Encode(state, delta > 0 ? 64 : 65, column, row, sgrRelease: false);
        }

        if (!state.AlternateScrollEnabled || !state.IsAlternateScreenActive)
        {
            return Unhandled();
        }

        string? sequence = delta > 0
            ? state.AlternateScrollUpSequence
            : state.AlternateScrollDownSequence;
        if (sequence is null)
        {
            return Unhandled();
        }

        int repeats = Math.Max(1, Math.Abs(delta) / wheelDelta);
        return new(true, TextPayload: string.Concat(Enumerable.Repeat(sequence, repeats)));
    }

    public bool ShouldCapture(TerminalMouseState state) => CanReport(state);

    public bool ShouldReleaseCapture(bool force, bool hasPressedButton) => force || !hasPressedButton;

    private static bool CanReport(TerminalMouseState state) =>
        state.SupportsInput && state.TrackingMode != TerminalMouseTrackingMode.Off;

    private static TerminalMouseAction Encode(
        TerminalMouseState state, int code, int column, int row, bool sgrRelease)
    {
        code += GetModifierBits(state.Modifiers);
        return new(true, TerminalInputEncoder.EncodeMouseSequence(state.Encoding, code, column, row, sgrRelease));
    }

    private static int GetModifierBits(TerminalMouseModifiers modifiers)
    {
        int bits = 0;
        if (modifiers.HasFlag(TerminalMouseModifiers.Shift)) bits |= 4;
        if (modifiers.HasFlag(TerminalMouseModifiers.Alt)) bits |= 8;
        if (modifiers.HasFlag(TerminalMouseModifiers.Control)) bits |= 16;
        return bits;
    }

    private static TerminalMouseAction Unhandled() => new(false);
}
