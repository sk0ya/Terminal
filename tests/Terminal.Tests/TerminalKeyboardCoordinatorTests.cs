using Terminal.Tabs;

namespace Terminal.Tests;

public sealed class TerminalKeyboardCoordinatorTests
{
    private readonly TerminalKeyboardCoordinator _coordinator = new();

    [Fact]
    public void ClipboardShortcutWinsWhileImeIsActiveAndProxyHasPendingText()
    {
        TerminalKeyboardAction action = Resolve(
            TerminalKeyboardSource.Proxy,
            TerminalKeyboardKey.V,
            TerminalKeyboardModifiers.Control,
            pending: true,
            ime: true);

        Assert.Equal(TerminalKeyboardActionKind.Paste, action.Kind);
    }

    [Fact]
    public void ProxyEnterWithPendingTextQueuesFlushWithoutHandlingKey()
    {
        TerminalKeyboardAction action = Resolve(
            TerminalKeyboardSource.Proxy,
            TerminalKeyboardKey.Enter,
            pending: true,
            enter: "\r");

        Assert.Equal(TerminalKeyboardActionKind.QueueProxyFlush, action.Kind);
    }

    [Fact]
    public void ImeSuppressesEncodedTerminalInput()
    {
        TerminalKeyboardAction action = Resolve(
            TerminalKeyboardSource.Output,
            TerminalKeyboardKey.Other,
            ime: true,
            special: "\u001b[kitty");

        Assert.Equal(TerminalKeyboardActionKind.PassThrough, action.Kind);
    }

    [Fact]
    public void OutputControlEncodingFlushesProxyBeforeSendingModifyOtherKeysSequence()
    {
        TerminalKeyboardAction action = Resolve(
            TerminalKeyboardSource.Output,
            TerminalKeyboardKey.Other,
            TerminalKeyboardModifiers.Control,
            control: "\u001b[27;5;120~");

        Assert.Equal(TerminalKeyboardActionKind.SendText, action.Kind);
        Assert.Equal("\u001b[27;5;120~", action.Text);
        Assert.True(action.FlushProxyFirst);
    }

    [Fact]
    public void KittySpecialEncodingIsPreservedAfterArbitration()
    {
        TerminalKeyboardAction action = Resolve(
            TerminalKeyboardSource.Proxy,
            TerminalKeyboardKey.Up,
            special: "\u001b[1;5A");

        Assert.Equal(TerminalKeyboardActionKind.SendText, action.Kind);
        Assert.Equal("\u001b[1;5A", action.Text);
    }

    [Fact]
    public void ApplicationKeypadHasPriorityOverSpecialEncoding()
    {
        TerminalKeyboardAction action = Resolve(
            TerminalKeyboardSource.Output,
            TerminalKeyboardKey.NumPad1,
            keypad: true,
            special: "fallback");

        Assert.Equal(TerminalKeyboardActionKind.SendText, action.Kind);
        Assert.Equal("\u001bOq", action.Text);
        Assert.True(action.FlushProxyFirst);
    }

    [Fact]
    public void WindowsModifierDoesNotTurnModifiedKeyIntoClipboardShortcut()
    {
        TerminalKeyboardAction action = Resolve(
            TerminalKeyboardSource.Output,
            TerminalKeyboardKey.V,
            TerminalKeyboardModifiers.Control | TerminalKeyboardModifiers.Windows,
            control: "\u0016");

        Assert.Equal(TerminalKeyboardActionKind.SendText, action.Kind);
    }

    [Fact]
    public void ImeProcessedEffectiveKeyDoesNotBecomeClipboardShortcut()
    {
        TerminalKeyboardAction action = Resolve(
            TerminalKeyboardSource.Proxy,
            TerminalKeyboardKey.V,
            TerminalKeyboardModifiers.Control,
            ime: true,
            shortcutKey: TerminalKeyboardKey.Other);

        Assert.Equal(TerminalKeyboardActionKind.PassThrough, action.Kind);
    }

    [Fact]
    public void OutputEnterStillFlushesProxyWhenNoSequenceCanBeEncoded()
    {
        TerminalKeyboardAction action = Resolve(
            TerminalKeyboardSource.Output,
            TerminalKeyboardKey.Enter);

        Assert.Equal(TerminalKeyboardActionKind.PassThrough, action.Kind);
        Assert.True(action.FlushProxyFirst);
    }

    [Fact]
    public void CommandScrollCanFallBackToTerminalSequenceWhenNoCommandIsFound()
    {
        TerminalKeyboardAction action = Resolve(
            TerminalKeyboardSource.Output,
            TerminalKeyboardKey.Up,
            TerminalKeyboardModifiers.Control | TerminalKeyboardModifiers.Shift,
            special: "\u001b[1;6A");

        Assert.Equal(TerminalKeyboardActionKind.ScrollPreviousCommand, action.Kind);
        Assert.Equal("\u001b[1;6A", action.Text);
        Assert.True(action.FlushProxyFirst);
    }

    [Fact]
    public void ConfiguredShortcutsReplaceBuiltInChordRatherThanAddingToIt()
    {
        TerminalKeyboardAction oldChord = _coordinator.Resolve(new(
            TerminalKeyboardSource.Output, TerminalKeyboardKey.V, TerminalKeyboardKey.V,
            TerminalKeyboardModifiers.Control, TerminalKeyboardModifiers.Control,
            HasSession: true, HasPendingProxyText: false, IsImeInput: false,
            SupportsTerminalInput: true, ApplicationKeypadEnabled: false,
            ControlSequence: "\u0016", EnterSequence: null, SpecialSequence: null,
            ConfiguredShortcut: null, UseConfiguredShortcuts: true));
        Assert.Equal(TerminalKeyboardActionKind.SendText, oldChord.Kind);

        TerminalKeyboardAction newChord = _coordinator.Resolve(new(
            TerminalKeyboardSource.Output, TerminalKeyboardKey.Other, TerminalKeyboardKey.Other,
            TerminalKeyboardModifiers.Alt, TerminalKeyboardModifiers.Alt,
            HasSession: true, HasPendingProxyText: false, IsImeInput: false,
            SupportsTerminalInput: true, ApplicationKeypadEnabled: false,
            ControlSequence: null, EnterSequence: null, SpecialSequence: null,
            ConfiguredShortcut: TerminalKeyboardActionKind.Paste, UseConfiguredShortcuts: true));
        Assert.Equal(TerminalKeyboardActionKind.Paste, newChord.Kind);
    }

    private TerminalKeyboardAction Resolve(
        TerminalKeyboardSource source,
        TerminalKeyboardKey key,
        TerminalKeyboardModifiers modifiers = TerminalKeyboardModifiers.None,
        bool pending = false,
        bool ime = false,
        bool keypad = false,
        string? control = null,
        string? enter = null,
        string? special = null,
        TerminalKeyboardKey? shortcutKey = null) =>
        _coordinator.Resolve(new(
            source, key, shortcutKey ?? key, modifiers,
            TerminalModifiers: modifiers & ~TerminalKeyboardModifiers.Windows,
            HasSession: true, pending, ime,
            SupportsTerminalInput: true, keypad, control, enter, special));
}
