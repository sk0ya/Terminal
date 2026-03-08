using System.IO;

namespace ConPtyTerminal;

internal static class TerminalProfileCatalog
{
    public static IReadOnlyList<TerminalProfileDefinition> CreateProfiles()
    {
        var profiles = new List<TerminalProfileDefinition>
        {
            new(
                "cmd",
                "Command Prompt",
                BuildDefaultCommandLine(),
                "Classic Windows shell with ConPTY support.")
        };

        if (TryBuildExecutableCommandLine("powershell.exe", "-NoLogo", out string windowsPowerShellCommandLine))
        {
            profiles.Add(new TerminalProfileDefinition(
                "powershell",
                "Windows PowerShell",
                windowsPowerShellCommandLine,
                "Windows PowerShell 5.1 profile."));
        }

        if (TryBuildExecutableCommandLine("pwsh.exe", "-NoLogo", out string powerShell7CommandLine))
        {
            profiles.Add(new TerminalProfileDefinition(
                "pwsh",
                "PowerShell 7",
                powerShell7CommandLine,
                "Modern PowerShell profile if pwsh is installed."));
        }

        if (TryBuildGitBashCommandLine(out string gitBashCommandLine))
        {
            profiles.Add(new TerminalProfileDefinition(
                "git-bash",
                "Git Bash",
                gitBashCommandLine,
                "Git for Windows bash login shell."));
        }

        return profiles;
    }

    public static string BuildDefaultCommandLine()
    {
        string? comSpec = Environment.GetEnvironmentVariable("ComSpec");
        if (!string.IsNullOrWhiteSpace(comSpec) && File.Exists(comSpec))
        {
            return $"\"{comSpec}\" /K";
        }

        return "cmd.exe /K";
    }

    internal static TerminalProfileDefinition ResolveSelectedProfile(
        IReadOnlyList<TerminalProfileDefinition> profiles,
        TerminalProfileDefinition customProfile,
        string? profileId,
        string? commandLine)
    {
        TerminalProfileDefinition? matchedProfile = MatchProfileByCommandLine(profiles, commandLine);
        if (matchedProfile is not null)
        {
            return matchedProfile;
        }

        TerminalProfileDefinition? profileById = profiles.FirstOrDefault(profile =>
            string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase));
        if (profileById is null)
        {
            return customProfile;
        }

        string normalizedCommandLine = NormalizeCommandLine(commandLine);
        if (!profileById.IsCustom &&
            normalizedCommandLine.Length > 0 &&
            !AreEquivalentCommandLines(profileById.CommandLine, normalizedCommandLine))
        {
            return customProfile;
        }

        return profileById;
    }

    private static bool TryBuildExecutableCommandLine(string executableName, string arguments, out string commandLine)
    {
        string? executablePath = TryFindExecutable(executableName);
        if (executablePath is null)
        {
            commandLine = string.Empty;
            return false;
        }

        commandLine = string.IsNullOrWhiteSpace(arguments)
            ? QuoteCommand(executablePath)
            : $"{QuoteCommand(executablePath)} {arguments}";
        return true;
    }

    internal static TerminalProfileDefinition? MatchProfileByCommandLine(
        IReadOnlyList<TerminalProfileDefinition> profiles,
        string? commandLine)
    {
        string normalizedCommandLine = NormalizeCommandLine(commandLine);
        if (normalizedCommandLine.Length == 0)
        {
            return null;
        }

        return profiles.FirstOrDefault(profile =>
            !profile.IsCustom &&
            AreEquivalentCommandLines(profile.CommandLine, normalizedCommandLine));
    }

    private static bool TryBuildGitBashCommandLine(out string commandLine)
    {
        string? executablePath = ResolveGitBashExecutable();

        if (executablePath is null)
        {
            commandLine = string.Empty;
            return false;
        }

        commandLine = $"{QuoteCommand(executablePath)} --login -i";
        return true;
    }

    internal static string? ResolveGitBashExecutable(
        string? pathValue = null,
        string? programFiles = null,
        string? programFilesX86 = null)
    {
        foreach (string candidate in EnumerateGitBashInstallCandidates(programFiles, programFilesX86))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return TryFindExecutable("bash.exe", pathValue, candidate => !IsWindowsSystemBash(candidate));
    }

    private static IEnumerable<string> EnumerateGitBashInstallCandidates(string? programFiles, string? programFilesX86)
    {
        string resolvedProgramFiles = string.IsNullOrWhiteSpace(programFiles)
            ? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
            : programFiles.Trim();
        string resolvedProgramFilesX86 = string.IsNullOrWhiteSpace(programFilesX86)
            ? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            : programFilesX86.Trim();

        if (!string.IsNullOrWhiteSpace(resolvedProgramFiles))
        {
            yield return Path.Combine(resolvedProgramFiles, "Git", "bin", "bash.exe");
        }

        if (!string.IsNullOrWhiteSpace(resolvedProgramFilesX86))
        {
            yield return Path.Combine(resolvedProgramFilesX86, "Git", "bin", "bash.exe");
        }
    }

    private static string? TryFindExecutable(
        string executableName,
        string? pathValue = null,
        Func<string, bool>? predicate = null)
    {
        if (string.IsNullOrWhiteSpace(executableName))
        {
            return null;
        }

        if (Path.IsPathRooted(executableName))
        {
            return File.Exists(executableName) ? Path.GetFullPath(executableName) : null;
        }

        string? effectivePathValue = string.IsNullOrWhiteSpace(pathValue)
            ? Environment.GetEnvironmentVariable("PATH")
            : pathValue;
        if (string.IsNullOrWhiteSpace(effectivePathValue))
        {
            return null;
        }

        foreach (string rawDirectory in effectivePathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string directory = rawDirectory.Trim().Trim('"');
            if (directory.Length == 0)
            {
                continue;
            }

            string candidatePath = Path.Combine(directory, executableName);
            if (File.Exists(candidatePath) && (predicate is null || predicate(candidatePath)))
            {
                return candidatePath;
            }
        }

        return null;
    }

    private static bool IsWindowsSystemBash(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        try
        {
            string candidatePath = Path.GetFullPath(executablePath);
            string systemBashPath = Path.GetFullPath(Path.Combine(Environment.SystemDirectory, "bash.exe"));
            return string.Equals(candidatePath, systemBashPath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string QuoteCommand(string path)
    {
        return path.Contains(' ') ? $"\"{path}\"" : path;
    }

    private static string NormalizeCommandLine(string? commandLine)
    {
        return string.IsNullOrWhiteSpace(commandLine)
            ? string.Empty
            : commandLine.Trim();
    }

    private static bool AreEquivalentCommandLines(string left, string right)
    {
        string normalizedLeft = NormalizeCommandLine(left);
        string normalizedRight = NormalizeCommandLine(right);
        if (normalizedLeft.Length == 0 || normalizedRight.Length == 0)
        {
            return false;
        }

        if (string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!TryParseCommandLine(normalizedLeft, out ParsedCommandLine leftCommandLine) ||
            !TryParseCommandLine(normalizedRight, out ParsedCommandLine rightCommandLine))
        {
            return false;
        }

        if (!AreEquivalentExecutables(leftCommandLine.ExecutablePath, rightCommandLine.ExecutablePath))
        {
            return false;
        }

        if (leftCommandLine.Arguments.Length != rightCommandLine.Arguments.Length)
        {
            return false;
        }

        for (int index = 0; index < leftCommandLine.Arguments.Length; index++)
        {
            if (!string.Equals(leftCommandLine.Arguments[index], rightCommandLine.Arguments[index], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryParseCommandLine(string commandLine, out ParsedCommandLine parsedCommandLine)
    {
        try
        {
            (string executable, string[] arguments) = ProcessPipeSession.SplitCommandLine(commandLine);
            parsedCommandLine = new ParsedCommandLine(
                ResolveExecutablePath(executable),
                arguments);
            return true;
        }
        catch
        {
            parsedCommandLine = new ParsedCommandLine(string.Empty, []);
            return false;
        }
    }

    private static string ResolveExecutablePath(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable))
        {
            return string.Empty;
        }

        string trimmedExecutable = executable.Trim();
        if (Path.IsPathRooted(trimmedExecutable))
        {
            try
            {
                return Path.GetFullPath(trimmedExecutable);
            }
            catch
            {
                return trimmedExecutable;
            }
        }

        return TryFindExecutable(trimmedExecutable) ?? trimmedExecutable;
    }

    private static bool AreEquivalentExecutables(string leftExecutable, string rightExecutable)
    {
        if (string.Equals(leftExecutable, rightExecutable, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string leftFileName = Path.GetFileName(leftExecutable);
        string rightFileName = Path.GetFileName(rightExecutable);
        return leftFileName.Length > 0 &&
            string.Equals(leftFileName, rightFileName, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ParsedCommandLine(string ExecutablePath, string[] Arguments);
}
