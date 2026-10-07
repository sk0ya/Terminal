namespace Terminal.Sessions;

/// <summary>
/// Everything a view decided about the session it is about to start. Passed to a host-supplied
/// session factory so the host can run the pty somewhere else (another process that outlives the
/// view) while the view keeps its launch logic: <see cref="LaunchCommandLine"/> already carries the
/// shell-integration injection.
/// </summary>
public sealed record TerminalSessionRequest(
    string CommandLine,
    string LaunchCommandLine,
    string WorkingDirectory,
    short Columns,
    short Rows,
    IReadOnlyDictionary<string, string?> EnvironmentVariables,
    int ScrollbackLimit);
