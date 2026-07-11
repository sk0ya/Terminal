namespace Terminal;

internal static class TerminalCloseConfirmation
{
    internal static bool NeedsConfirmation(IEnumerable<bool> tabStates) =>
        tabStates.Any(static requiresConfirmation => requiresConfirmation);
}
