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
}
