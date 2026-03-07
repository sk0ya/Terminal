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
        string? executablePath = TryFindExecutable("bash.exe");
        if (executablePath is null)
        {
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string[] candidates =
            [
                Path.Combine(programFiles, "Git", "bin", "bash.exe"),
                Path.Combine(programFilesX86, "Git", "bin", "bash.exe")
            ];

            executablePath = candidates.FirstOrDefault(File.Exists);
        }

        if (executablePath is null)
        {
            commandLine = string.Empty;
            return false;
        }

        commandLine = $"{QuoteCommand(executablePath)} --login -i";
        return true;
    }

    private static string? TryFindExecutable(string executableName)
    {
        if (string.IsNullOrWhiteSpace(executableName))
        {
            return null;
        }

        if (Path.IsPathRooted(executableName))
        {
            return File.Exists(executableName) ? Path.GetFullPath(executableName) : null;
        }

        string? pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        foreach (string rawDirectory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string directory = rawDirectory.Trim().Trim('"');
            if (directory.Length == 0)
            {
                continue;
            }

            string candidatePath = Path.Combine(directory, executableName);
            if (File.Exists(candidatePath))
            {
                return candidatePath;
            }
        }

        return null;
    }

    private static string QuoteCommand(string path)
    {
        return path.Contains(' ') ? $"\"{path}\"" : path;
    }
}
