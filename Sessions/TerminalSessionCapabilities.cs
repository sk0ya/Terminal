namespace Terminal.Sessions;

public enum TerminalSessionKind
{
    ConPty
}

public readonly record struct TerminalSessionCapabilities(
    TerminalSessionKind Kind,
    bool SupportsResize,
    bool SupportsTerminalInput)
{
    public string DisplayName => "ConPTY";
}
