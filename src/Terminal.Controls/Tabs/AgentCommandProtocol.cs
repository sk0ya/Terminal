using System.Globalization;
using System.Text.RegularExpressions;

namespace Terminal.Tabs;

/// <summary>
/// Identifies the interactive shell hosting a session so the sentinel fallback
/// (used when OSC 133 shell integration is unavailable) can emit completion
/// markers in the right dialect.
/// </summary>
internal enum AgentShellKind
{
    Unknown,
    PowerShell,
    Cmd,
    Bash
}

/// <summary>
/// Pure, side-effect-free helpers backing <see cref="TerminalTabView.RunCommandAsync"/>'s
/// sentinel fallback: shell detection, the marker-bearing command line to submit, and
/// parsing the bracketed output/exit code back out of the terminal text.
/// </summary>
internal static class AgentCommandProtocol
{
    private const string BeginPrefix = "__ASE_B_";
    private const string EndPrefix = "__ASE_E_";

    internal static AgentShellKind DetectShellKind(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return AgentShellKind.Unknown;
        }

        string lower = commandLine.ToLowerInvariant();
        if (lower.Contains("pwsh") || lower.Contains("powershell"))
        {
            return AgentShellKind.PowerShell;
        }

        if (lower.Contains("bash") || lower.Contains("/sh") || lower.Contains("\\sh") || lower.Contains("zsh"))
        {
            return AgentShellKind.Bash;
        }

        if (lower.Contains("cmd"))
        {
            return AgentShellKind.Cmd;
        }

        return AgentShellKind.Unknown;
    }

    internal static string NewMarkerId()
    {
        // Short enough that the printed marker line does not wrap even on a narrow
        // terminal (which would split the token and defeat parsing), while 32 bits of
        // randomness make a collision with command output text negligible per session.
        return Guid.NewGuid().ToString("N")[..8];
    }

    /// <summary>
    /// Builds a single line that prints a BEGIN marker, runs the command, then prints an
    /// END marker carrying the command's exit code. The command output is therefore
    /// bracketed by two marker lines that survive line-wrapping and ANSI styling, while
    /// the exit code is captured immediately after the command so it is not clobbered.
    /// </summary>
    internal static string BuildSentinelCommand(AgentShellKind shell, string command, string markerId)
    {
        string begin = BeginPrefix + markerId;
        string end = EndPrefix + markerId + "_";

        return shell switch
        {
            // $? must be captured before any other statement (an assignment resets it).
            // On failure prefer a non-zero $LASTEXITCODE, but fall back to 1 because
            // $LASTEXITCODE is only set by native exes and may be a stale 0 after a
            // cmdlet failure.
            AgentShellKind.PowerShell =>
                $"Write-Output '{begin}'; {command}; $__aseok=$?; $__aseec=$LASTEXITCODE; " +
                $"Write-Output ('{end}' + $(if ($__aseok) {{ 0 }} elseif ($__aseec) {{ $__aseec }} else {{ 1 }}))",

            AgentShellKind.Bash =>
                $"echo {begin}; {command}; __aseec=$?; echo {end}${{__aseec}}",

            // cmd expands %errorlevel% at parse time, so the whole chain runs inside one
            // child shell with delayed expansion enabled; !errorlevel! is then evaluated
            // after the command actually runs (a separate child cmd would not inherit it).
            AgentShellKind.Cmd =>
                $"cmd /v:on /c \"echo {begin}&{command}&echo {end}!errorlevel!\"",

            _ => command
        };
    }

    /// <summary>
    /// Scans terminal text (ANSI-free, e.g. a buffer plain-text snapshot) for the printed
    /// BEGIN/END markers and, when both are present, returns the bracketed output and the
    /// parsed exit code. Returns false while the command is still running.
    /// </summary>
    internal static bool TryParseCompletedOutput(string text, string markerId, out string output, out int exitCode)
    {
        output = string.Empty;
        exitCode = -1;

        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(markerId))
        {
            return false;
        }

        string beginToken = BeginPrefix + markerId;
        string endPrefix = EndPrefix + markerId + "_";

        // The printed END marker sits at the start of its own line and is followed by an
        // actual integer; the echoed command carries the marker mid-line with an
        // un-evaluated expression after it. Anchoring to line start plus requiring digits
        // excludes the echo, and taking the last match excludes any earlier look-alike.
        var endRegex = new Regex(@"(?m)^[^\S\r\n]*" + Regex.Escape(endPrefix) + @"(-?\d+)");
        Match? endMatch = null;
        foreach (Match candidate in endRegex.Matches(text))
        {
            endMatch = candidate;
        }

        if (endMatch is null)
        {
            return false;
        }

        if (!int.TryParse(endMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out exitCode))
        {
            exitCode = -1;
        }

        // The printed BEGIN marker sits on its own line (the echoed input has trailing
        // quotes/operators after the token, never a newline).
        int beginIndex = FindPrintedBeginMarker(text, beginToken, endMatch.Index);
        if (beginIndex < 0)
        {
            return false;
        }

        int outStart = beginIndex + beginToken.Length;
        outStart = SkipTrailingSpaces(text, outStart);
        if (outStart < text.Length && text[outStart] == '\r')
        {
            outStart++;
        }

        if (outStart < text.Length && text[outStart] == '\n')
        {
            outStart++;
        }

        int outEnd = endMatch.Index;
        string raw = outStart <= outEnd ? text[outStart..outEnd] : string.Empty;
        output = raw.TrimEnd('\r', '\n');
        return true;
    }

    private static int FindPrintedBeginMarker(string text, string beginToken, int searchLimit)
    {
        int printed = -1;
        int index = 0;
        while (true)
        {
            int found = text.IndexOf(beginToken, index, StringComparison.Ordinal);
            if (found < 0 || found >= searchLimit)
            {
                break;
            }

            int after = SkipTrailingSpaces(text, found + beginToken.Length);
            if (after < text.Length && (text[after] == '\r' || text[after] == '\n'))
            {
                printed = found;
            }

            index = found + beginToken.Length;
        }

        return printed;
    }

    private static int SkipTrailingSpaces(string text, int index)
    {
        while (index < text.Length && (text[index] == ' ' || text[index] == '\t'))
        {
            index++;
        }

        return index;
    }
}
