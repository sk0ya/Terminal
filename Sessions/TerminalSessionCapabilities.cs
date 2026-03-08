namespace Terminal.Sessions;

public enum TerminalSessionKind
{
    ConPty,
    Compatibility
}

public readonly record struct TerminalSessionCapabilities(
    TerminalSessionKind Kind,
    bool SupportsResize,
    bool SupportsTerminalInput)
{
    public string DisplayName => Kind == TerminalSessionKind.ConPty ? "ConPTY" : "Compat";
}
