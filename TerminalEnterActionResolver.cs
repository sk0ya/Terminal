using System.Windows.Input;

namespace Terminal;

internal enum TerminalEnterAction
{
    None,
    FlushPendingProxyText,
    SendToTerminal
}

internal static class TerminalEnterActionResolver
{
    public static TerminalEnterAction ResolveForProxy(Key key, bool hasPendingProxyText)
    {
        if (key != Key.Enter)
        {
            return TerminalEnterAction.None;
        }

        return hasPendingProxyText
            ? TerminalEnterAction.FlushPendingProxyText
            : TerminalEnterAction.SendToTerminal;
    }
}
