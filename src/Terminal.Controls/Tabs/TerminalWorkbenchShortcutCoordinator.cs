namespace Terminal.Tabs;

internal enum TerminalWorkbenchShortcutKey
{
    Other,
    S,
    R,
    Add,
    OemPlus,
    Subtract,
    OemMinus,
    D0,
    NumPad0
}

[Flags]
internal enum TerminalWorkbenchShortcutModifiers
{
    None = 0,
    Shift = 1,
    Control = 2,
    Alt = 4,
    Windows = 8
}

internal enum TerminalWorkbenchShortcutAction
{
    None,
    SaveTranscript,
    Restart,
    IncreaseFontSize,
    DecreaseFontSize,
    ResetFontSize
}

internal static class TerminalWorkbenchShortcutCoordinator
{
    public static TerminalWorkbenchShortcutAction Resolve(
        TerminalWorkbenchShortcutKey key,
        TerminalWorkbenchShortcutModifiers modifiers) =>
        (key, modifiers) switch
        {
            (TerminalWorkbenchShortcutKey.S,
                TerminalWorkbenchShortcutModifiers.Control | TerminalWorkbenchShortcutModifiers.Shift) =>
                TerminalWorkbenchShortcutAction.SaveTranscript,
            (TerminalWorkbenchShortcutKey.R,
                TerminalWorkbenchShortcutModifiers.Control | TerminalWorkbenchShortcutModifiers.Shift) =>
                TerminalWorkbenchShortcutAction.Restart,
            (TerminalWorkbenchShortcutKey.Add or TerminalWorkbenchShortcutKey.OemPlus,
                TerminalWorkbenchShortcutModifiers.Control) =>
                TerminalWorkbenchShortcutAction.IncreaseFontSize,
            (TerminalWorkbenchShortcutKey.Subtract or TerminalWorkbenchShortcutKey.OemMinus,
                TerminalWorkbenchShortcutModifiers.Control) =>
                TerminalWorkbenchShortcutAction.DecreaseFontSize,
            (TerminalWorkbenchShortcutKey.D0 or TerminalWorkbenchShortcutKey.NumPad0,
                TerminalWorkbenchShortcutModifiers.Control) =>
                TerminalWorkbenchShortcutAction.ResetFontSize,
            _ => TerminalWorkbenchShortcutAction.None
        };
}
