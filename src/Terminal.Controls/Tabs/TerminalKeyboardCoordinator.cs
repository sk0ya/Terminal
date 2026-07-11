namespace Terminal.Tabs;

[Flags]
internal enum TerminalKeyboardModifiers { None = 0, Shift = 1, Control = 2, Alt = 4, Windows = 8 }
internal enum TerminalKeyboardSource { Output, Proxy }
internal enum TerminalKeyboardKey
{
    Other, Enter, C, V, R, F, Insert, Up, Down, Left, Right,
    NumPad0, NumPad1, NumPad2, NumPad3, NumPad4, NumPad5, NumPad6, NumPad7,
    NumPad8, NumPad9, Multiply, Add, Separator, Subtract, Decimal, Divide
}
internal enum TerminalKeyboardActionKind
{
    PassThrough, Copy, Paste, ScrollPreviousCommand, ScrollNextCommand,
    OpenHistory, OpenFind, LocalSelection, QueueProxyFlush, Interrupt, SendText
}

internal sealed record TerminalKeyboardRequest(
    TerminalKeyboardSource Source,
    TerminalKeyboardKey Key,
    TerminalKeyboardKey ShortcutKey,
    TerminalKeyboardModifiers Modifiers,
    TerminalKeyboardModifiers TerminalModifiers,
    bool HasSession,
    bool HasPendingProxyText,
    bool IsImeInput,
    bool SupportsTerminalInput,
    bool ApplicationKeypadEnabled,
    string? ControlSequence,
    string? EnterSequence,
    string? SpecialSequence,
    TerminalKeyboardActionKind? ConfiguredShortcut = null,
    bool UseConfiguredShortcuts = false);

internal sealed record TerminalKeyboardAction(
    TerminalKeyboardActionKind Kind,
    string? Text = null,
    bool FlushProxyFirst = false);

internal sealed class TerminalKeyboardCoordinator
{
    public TerminalKeyboardAction Resolve(TerminalKeyboardRequest request)
    {
        if (!request.HasSession)
        {
            return Pass();
        }

        TerminalKeyboardAction? clipboard = request.UseConfiguredShortcuts
            ? ResolveConfiguredShortcut(request.ConfiguredShortcut, request.ShortcutKey, request.Modifiers)
            : ResolveClipboard(request.ShortcutKey, request.Modifiers);
        if (clipboard is not null)
        {
            if (clipboard.Kind is TerminalKeyboardActionKind.ScrollPreviousCommand or
                TerminalKeyboardActionKind.ScrollNextCommand &&
                !request.HasPendingProxyText && !request.IsImeInput)
            {
                return clipboard with
                {
                    Text = request.ControlSequence ?? request.SpecialSequence,
                    FlushProxyFirst = request.Source == TerminalKeyboardSource.Output
                };
            }

            return clipboard;
        }

        if (request.Source == TerminalKeyboardSource.Output &&
            request.Modifiers.HasFlag(TerminalKeyboardModifiers.Shift) &&
            !request.Modifiers.HasFlag(TerminalKeyboardModifiers.Control) &&
            request.Key is TerminalKeyboardKey.Up or TerminalKeyboardKey.Down or
                TerminalKeyboardKey.Left or TerminalKeyboardKey.Right)
        {
            return new(TerminalKeyboardActionKind.LocalSelection);
        }

        if (request.Source == TerminalKeyboardSource.Proxy && request.Key == TerminalKeyboardKey.Enter)
        {
            if (request.HasPendingProxyText)
            {
                return new(TerminalKeyboardActionKind.QueueProxyFlush);
            }

            return Send(request.EnterSequence);
        }

        if (request.Source == TerminalKeyboardSource.Proxy && request.HasPendingProxyText)
        {
            return Pass();
        }

        if (request.IsImeInput)
        {
            return Pass();
        }

        if (request.Source == TerminalKeyboardSource.Output && request.Key == TerminalKeyboardKey.Enter)
        {
            return request.EnterSequence is null
                ? Pass(flush: true)
                : Send(request.EnterSequence);
        }

        bool flush = request.Source == TerminalKeyboardSource.Output;
        if (request.ControlSequence is not null)
        {
            return request.Key == TerminalKeyboardKey.C &&
                request.TerminalModifiers == TerminalKeyboardModifiers.Control
                ? new(TerminalKeyboardActionKind.Interrupt, FlushProxyFirst: flush)
                : new(TerminalKeyboardActionKind.SendText, request.ControlSequence, flush);
        }

        string? keypad = request.SupportsTerminalInput &&
            request.ApplicationKeypadEnabled &&
            request.Modifiers == TerminalKeyboardModifiers.None
            ? ResolveKeypad(request.Key)
            : null;
        if (keypad is not null)
        {
            return new(TerminalKeyboardActionKind.SendText, keypad, flush);
        }

        return request.SpecialSequence is null
            ? Pass(flush)
            : new(TerminalKeyboardActionKind.SendText, request.SpecialSequence, flush);
    }

    private static TerminalKeyboardAction? ResolveClipboard(
        TerminalKeyboardKey key,
        TerminalKeyboardModifiers modifiers) => (key, modifiers) switch
    {
        (TerminalKeyboardKey.C, TerminalKeyboardModifiers.Control | TerminalKeyboardModifiers.Shift) => new(TerminalKeyboardActionKind.Copy),
        (TerminalKeyboardKey.Insert, TerminalKeyboardModifiers.Control) => new(TerminalKeyboardActionKind.Copy),
        (TerminalKeyboardKey.V, TerminalKeyboardModifiers.Control) => new(TerminalKeyboardActionKind.Paste),
        (TerminalKeyboardKey.V, TerminalKeyboardModifiers.Control | TerminalKeyboardModifiers.Shift) => new(TerminalKeyboardActionKind.Paste),
        (TerminalKeyboardKey.Insert, TerminalKeyboardModifiers.Shift) => new(TerminalKeyboardActionKind.Paste),
        (TerminalKeyboardKey.Up, TerminalKeyboardModifiers.Control | TerminalKeyboardModifiers.Shift) => new(TerminalKeyboardActionKind.ScrollPreviousCommand),
        (TerminalKeyboardKey.Down, TerminalKeyboardModifiers.Control | TerminalKeyboardModifiers.Shift) => new(TerminalKeyboardActionKind.ScrollNextCommand),
        (TerminalKeyboardKey.R, TerminalKeyboardModifiers.Control) => new(TerminalKeyboardActionKind.OpenHistory),
        (TerminalKeyboardKey.F, TerminalKeyboardModifiers.Control | TerminalKeyboardModifiers.Shift) => new(TerminalKeyboardActionKind.OpenFind),
        _ => null
    };

    private static TerminalKeyboardAction? ResolveConfiguredShortcut(
        TerminalKeyboardActionKind? configured,
        TerminalKeyboardKey key,
        TerminalKeyboardModifiers modifiers)
    {
        if (configured.HasValue) return new(configured.Value);
        return (key, modifiers) switch
        {
            (TerminalKeyboardKey.Insert, TerminalKeyboardModifiers.Control) => new(TerminalKeyboardActionKind.Copy),
            (TerminalKeyboardKey.Insert, TerminalKeyboardModifiers.Shift) => new(TerminalKeyboardActionKind.Paste),
            _ => null
        };
    }

    private static TerminalKeyboardAction Pass(bool flush = false) =>
        new(TerminalKeyboardActionKind.PassThrough, FlushProxyFirst: flush);

    private static TerminalKeyboardAction Send(string? text) => text is null
        ? Pass()
        : new(TerminalKeyboardActionKind.SendText, text);

    private static string? ResolveKeypad(TerminalKeyboardKey key) => key switch
    {
        TerminalKeyboardKey.NumPad0 => "\u001bOp", TerminalKeyboardKey.NumPad1 => "\u001bOq",
        TerminalKeyboardKey.NumPad2 => "\u001bOr", TerminalKeyboardKey.NumPad3 => "\u001bOs",
        TerminalKeyboardKey.NumPad4 => "\u001bOt", TerminalKeyboardKey.NumPad5 => "\u001bOu",
        TerminalKeyboardKey.NumPad6 => "\u001bOv", TerminalKeyboardKey.NumPad7 => "\u001bOw",
        TerminalKeyboardKey.NumPad8 => "\u001bOx", TerminalKeyboardKey.NumPad9 => "\u001bOy",
        TerminalKeyboardKey.Multiply => "\u001bOj", TerminalKeyboardKey.Add => "\u001bOk",
        TerminalKeyboardKey.Separator => "\u001bOl", TerminalKeyboardKey.Subtract => "\u001bOm",
        TerminalKeyboardKey.Decimal => "\u001bOn", TerminalKeyboardKey.Divide => "\u001bOo",
        _ => null
    };
}
