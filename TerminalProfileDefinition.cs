namespace Terminal;

internal sealed record TerminalProfileDefinition(
    string Id,
    string DisplayName,
    string CommandLine,
    string Description,
    bool IsCustom = false);
