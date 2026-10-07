using System.Windows;
using System.Windows.Input;

using Terminal.Input;

namespace Terminal.Tabs;

/// <summary>
/// What a piece of user input was before this pane encoded it, so another pane can encode it for
/// its own input modes: a key with its modifiers, or the text of a paste. Typed text carries
/// neither and is mirrored as sent.
/// </summary>
internal readonly record struct TerminalInputOrigin(Key? Key, ModifierKeys Modifiers, string? PastedText)
{
    public static TerminalInputOrigin FromKey(Key key, ModifierKeys modifiers) => new(key, modifiers, null);

    public static TerminalInputOrigin FromPaste(string text) => new(null, ModifierKeys.None, text);
}

/// <summary>Event data for <see cref="TerminalTabView.UserInputSent"/>.</summary>
public sealed class TerminalUserInputEventArgs : EventArgs
{
    internal TerminalUserInputEventArgs(string text, TerminalInputOrigin? origin = null)
    {
        Text = text;
        Origin = origin;
    }

    /// <summary>The bytes sent to the session, as text (already encoded for this pane's input modes).</summary>
    public string Text { get; }

    internal TerminalInputOrigin? Origin { get; }
}

/// <summary>
/// Input broadcast support: the tab reports what the human typed or pasted, and shows a frame
/// while the host is mirroring that input to other panes.
/// </summary>
public partial class TerminalTabView
{
    /// <summary>
    /// Raised after input the user typed or pasted (keys, text, paste, Ctrl+C from the menu) was
    /// written to the session. Not raised for terminal replies, mouse or focus reports, or input sent
    /// through <see cref="SendTerminalInput(string)"/> — so a host can mirror it to other panes with
    /// <see cref="SendMirroredInput(string)"/> without echoing back.
    /// </summary>
    public event EventHandler<TerminalUserInputEventArgs>? UserInputSent;

    /// <summary>Shows a frame around the terminal to say its input is being broadcast to other panes.</summary>
    public bool IsInputBroadcastIndicatorVisible
    {
        get => BroadcastIndicatorOverlay.Visibility == Visibility.Visible;
        set => BroadcastIndicatorOverlay.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Sends input mirrored from another pane: written like typed input (the view returns to the
    /// live screen) but without raising <see cref="UserInputSent"/>, so mirroring never echoes back.
    /// </summary>
    public bool SendMirroredInput(string text)
    {
        if (!SendTerminalInput(text))
        {
            return false;
        }

        ScrollToLiveScreen();
        return true;
    }

    /// <summary>
    /// Mirrors another pane's input, re-encoded for this pane: keys use this pane's cursor-key,
    /// keypad, modifyOtherKeys and kitty modes, and a paste is bracketed only if this pane asked
    /// for bracketed paste. Typed text is sent as it was.
    /// </summary>
    public bool SendMirroredInput(TerminalUserInputEventArgs input)
    {
        ArgumentNullException.ThrowIfNull(input);
        string? text = input.Origin switch
        {
            { Key: { } key } origin => EncodeKeyForMirror(key, origin.Modifiers),
            { PastedText: { } pasted } => EncodePasteForMirror(pasted),
            _ => input.Text
        };
        return text is not null && SendMirroredInput(text);
    }

    /// <summary>This pane's bytes for <paramref name="key"/>, the way its own key handling would send them.</summary>
    internal string? EncodeKeyForMirror(Key key, ModifierKeys modifiers)
    {
        bool supportsInput = SupportsTerminalInput();
        if (key == Key.Enter)
        {
            return TerminalKeyChordTranslator.TranslateEnterKey(
                modifiers,
                _terminalBuffer.ApplicationCursorKeysEnabled,
                supportsInput,
                _terminalBuffer.ModifyOtherKeysLevel,
                _terminalBuffer.KittyKeyboardFlags);
        }

        if (supportsInput && (modifiers & ModifierKeys.Control) != 0 &&
            TerminalKeyChordTranslator.TranslateCtrlChord(key, modifiers, _terminalBuffer.ModifyOtherKeysLevel) is { } chord)
        {
            return chord;
        }

        if (supportsInput && _terminalBuffer.ApplicationKeypadEnabled && modifiers == ModifierKeys.None &&
            TerminalKeyboardCoordinator.ResolveKeypad(MapKeyboardKey(key)) is { } keypad)
        {
            return keypad;
        }

        bool specialRequiresInput = key is not Key.Back and not Key.Tab and not Key.Escape;
        return !specialRequiresInput || supportsInput
            ? TerminalKeyChordTranslator.TranslateSpecialKey(
                key,
                modifiers,
                _terminalBuffer.ApplicationCursorKeysEnabled,
                _terminalBuffer.ModifyOtherKeysLevel,
                _terminalBuffer.KittyKeyboardFlags)
            : null;
    }

    private string? EncodePasteForMirror(string pasted)
    {
        // The user already confirmed a multi-line paste in the pane they pasted into.
        TerminalPasteAction action = _clipboardState.ResolvePaste(
            hasSession: _session is not null,
            clipboardContainsText: true,
            pasted,
            _terminalBuffer.BracketedPasteEnabled,
            multilinePasteApproved: true);
        return action is { Kind: TerminalPasteActionKind.Send, Text: not null } ? action.Text : null;
    }

    /// <summary>The key a keyboard event stands for, with the modifiers the terminal encodes.</summary>
    private TerminalInputOrigin KeyOrigin(KeyEventArgs e) =>
        TerminalInputOrigin.FromKey(
            GetEffectiveKey(e),
            GetTerminalModifiers(includeWindows: (_terminalBuffer.KittyKeyboardFlags & 0x08) != 0));

    private void RaiseUserInputSent(string text, TerminalInputOrigin? origin) =>
        UserInputSent?.Invoke(this, new TerminalUserInputEventArgs(text, origin));
}
