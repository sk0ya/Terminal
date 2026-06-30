using System.IO;

namespace Terminal.Sessions;

/// <summary>
/// Rewrites a pwsh (PowerShell 7+) launch so the shell emits OSC 133
/// shell-integration markers (prompt / command / output zones) without the
/// user editing their profile. Other shells launch unchanged. Failures fall
/// back to the original launch so a session always starts even when the
/// integration cannot be provisioned.
/// </summary>
public static class ShellIntegration
{
    private const string ScriptFileName = "shell-integration.ps1";

    /// <summary>
    /// Arguments that mean pwsh is being launched to run something rather
    /// than as an interactive shell; injecting -Command there would override
    /// or conflict with the caller's intent.
    /// </summary>
    private static readonly string[] PowerShellNonInteractiveArguments =
    [
        "-command", "-c", "-encodedcommand", "-enc", "-e", "-ec", "-file", "-f"
    ];

    /// <summary>
    /// Dot-sourced into pwsh at startup. Wraps the active prompt function to
    /// bracket it with OSC 133 D (previous command done, with exit code),
    /// A (prompt start), and B (command input start); wraps PSReadLine's
    /// PSConsoleHostReadLine to emit C (command executed, output follows).
    /// </summary>
    internal const string PowerShellScript =
        """
        # OSC 133 shell integration for ConPTY Terminal. Injected at session startup;
        # wraps the existing prompt so prompt/command/output zones are marked for the
        # hosting terminal. Safe to dot-source more than once.
        if ($global:__ConPtyTerminalShellIntegration) { return }
        $global:__ConPtyTerminalShellIntegration = $true
        $global:__ConPtyTerminalOriginalPrompt = $function:prompt

        function global:__ConPtyTerminalEncodeCommandLine([string]$text) {
            # Escapes backslash, the ';' field separator, and control characters
            # (incl. newlines) as \xNN so the command survives one OSC 633;E line.
            # Must match DecodeShellCommandLine in AnsiTerminalBuffer.
            if ([string]::IsNullOrEmpty($text)) { return '' }
            $sb = [System.Text.StringBuilder]::new($text.Length)
            foreach ($ch in $text.ToCharArray()) {
                $code = [int]$ch
                if ($ch -eq '\') { [void]$sb.Append('\\') }
                elseif ($code -lt 0x20 -or $code -eq 0x7f -or $ch -eq ';') {
                    [void]$sb.Append(('\x{0:x2}' -f $code))
                } else {
                    [void]$sb.Append($ch)
                }
            }
            $sb.ToString()
        }

        function global:__ConPtyTerminalEnsureReadLineHook {
            if ($global:__ConPtyTerminalReadLineHooked) { return }
            $readLine = Get-Command -Name PSConsoleHostReadLine -CommandType Function -ErrorAction SilentlyContinue
            if ($null -eq $readLine) { return }
            $global:__ConPtyTerminalReadLineHooked = $true
            $global:__ConPtyTerminalOriginalReadLine = $readLine.ScriptBlock
            # Report the real PSReadLine history file so the host can seed Ctrl+R
            # search from previous sessions. Best-effort; ignored if unavailable.
            if (-not $global:__ConPtyTerminalHistoryPathSent) {
                try {
                    $histPath = (Get-PSReadLineOption).HistorySavePath
                    if ($histPath) {
                        $global:__ConPtyTerminalHistoryPathSent = $true
                        $encodedPath = __ConPtyTerminalEncodeCommandLine $histPath
                        [Console]::Write("$([char]27)]633;P;HistoryPath=$encodedPath$([char]7)")
                    }
                } catch { }
            }
            # Emits 633;E (the command line text) then 133;C (command executed) right
            # after the user submits a line, so the terminal knows the command and
            # where its output starts.
            function global:PSConsoleHostReadLine {
                $line = & $global:__ConPtyTerminalOriginalReadLine
                $esc = [char]27
                $bel = [char]7
                $encoded = __ConPtyTerminalEncodeCommandLine ([string]$line)
                [Console]::Write("$esc]633;E;$encoded$bel$esc]133;C$bel")
                $line
            }
        }

        function global:prompt {
            # $? reflects the last command only until another statement runs.
            $commandSucceeded = $?
            $exitCode = 0
            if (-not $commandSucceeded) {
                if ($global:LASTEXITCODE -is [int] -and $global:LASTEXITCODE -ne 0) {
                    $exitCode = $global:LASTEXITCODE
                } else {
                    # Cmdlet failures leave $LASTEXITCODE stale, so report a generic failure.
                    $exitCode = 1
                }
            }
            # PSReadLine may load after this script runs; keep trying until hooked.
            __ConPtyTerminalEnsureReadLineHook
            $promptText = if ($null -ne $global:__ConPtyTerminalOriginalPrompt) {
                (& $global:__ConPtyTerminalOriginalPrompt) -join ''
            } else {
                "PS $($executionContext.SessionState.Path.CurrentLocation)> "
            }
            $esc = [char]27
            $bel = [char]7
            "$esc]133;D;$exitCode$bel$esc]133;A$bel$promptText$esc]133;B$bel"
        }

        __ConPtyTerminalEnsureReadLineHook

        """;

    /// <summary>
    /// Where the integration script is provisioned for injected sessions.
    /// </summary>
    public static string DefaultScriptPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Terminal",
        ScriptFileName);

    /// <summary>
    /// True when <see cref="PrepareLaunch(string)"/> would rewrite the given
    /// command line: an interactive pwsh launch without -Command/-File style
    /// arguments.
    /// </summary>
    public static bool CanInject(string? commandLine)
    {
        return TryParseInjectablePowerShell(commandLine);
    }

    /// <summary>
    /// Returns the command line to launch so the session emits OSC 133
    /// markers, provisioning the integration script on disk as a side effect.
    /// Returns the original command line when injection does not apply or the
    /// script cannot be written.
    /// </summary>
    public static string PrepareLaunch(string commandLine)
    {
        return PrepareLaunch(commandLine, scriptDirectory: null);
    }

    internal static string PrepareLaunch(string commandLine, string? scriptDirectory)
    {
        if (!TryParseInjectablePowerShell(commandLine))
        {
            return commandLine;
        }

        string scriptPath;
        try
        {
            scriptPath = EnsurePowerShellScript(scriptDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return commandLine;
        }

        return AppendPowerShellInjection(commandLine, scriptPath);
    }

    private static bool TryParseInjectablePowerShell(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return false;
        }

        string fileName;
        string[] arguments;
        try
        {
            (fileName, arguments) = TerminalCommandLine.SplitCommandLine(commandLine);
        }
        catch (ArgumentException)
        {
            return false;
        }

        string executable = Path.GetFileNameWithoutExtension(fileName.Trim('"'));
        return executable.Equals("pwsh", StringComparison.OrdinalIgnoreCase) &&
            ShouldInjectPowerShell(arguments);
    }

    internal static bool ShouldInjectPowerShell(IReadOnlyList<string> arguments)
    {
        foreach (string argument in arguments)
        {
            string normalized = argument.Trim().ToLowerInvariant();
            if (PowerShellNonInteractiveArguments.Contains(normalized))
            {
                return false;
            }
        }

        return true;
    }

    internal static string AppendPowerShellInjection(string commandLine, string scriptPath)
    {
        string escapedPath = scriptPath.Replace("'", "''");
        return $"{commandLine.TrimEnd()} -NoExit -Command \". '{escapedPath}'\"";
    }

    internal static string EnsurePowerShellScript(string? scriptDirectory = null)
    {
        string path = scriptDirectory is null
            ? DefaultScriptPath
            : Path.Combine(scriptDirectory, ScriptFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path) || File.ReadAllText(path) != PowerShellScript)
        {
            File.WriteAllText(path, PowerShellScript);
        }

        return path;
    }
}
