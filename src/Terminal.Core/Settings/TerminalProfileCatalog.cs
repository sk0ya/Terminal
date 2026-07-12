using System.IO;
using System.Text;
using Microsoft.Win32;

using Terminal.Sessions;

namespace Terminal.Settings;

public static class TerminalProfileCatalog
{
    private static readonly object WslSync = new();
    private static WslDiscoverySnapshot _wslSnapshot = new(ResolveWslExecutable(), [], null, DateTimeOffset.MinValue);
    private static Task? _wslRefreshTask;
    private static readonly TimeSpan WslCacheTtl = TimeSpan.FromMinutes(5);
    public static event EventHandler? ProfilesChanged;
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

        AddWslProfiles(profiles);

        return profiles;
    }

    private static void AddWslProfiles(List<TerminalProfileDefinition> profiles)
    {
        WslDiscoverySnapshot snapshot;
        lock (WslSync)
        {
            snapshot = _wslSnapshot;
            if (ShouldRefreshWsl(snapshot.NextRefreshAt, DateTimeOffset.UtcNow))
                _wslRefreshTask ??= Task.Run(RefreshWslAsync);
        }
        if (snapshot.Executable is null) return;
        // wsl.exe without --distribution is useful only when the registered default
        // points at a distribution which survived validation.
        if (snapshot.DefaultDistribution is not null)
            profiles.Add(new TerminalProfileDefinition(
                "wsl", "WSL", QuoteCommand(snapshot.Executable), "Default Windows Subsystem for Linux distribution."));
        foreach (string distribution in snapshot.Distributions)
        {
            profiles.Add(new TerminalProfileDefinition(
                BuildWslProfileId(distribution),
                distribution,
                BuildWslCommandLine(snapshot.Executable, distribution),
                distribution.Equals(snapshot.DefaultDistribution, StringComparison.OrdinalIgnoreCase)
                    ? $"WSL distribution: {distribution} (default)."
                    : $"WSL distribution: {distribution}."));
        }
    }

    internal static bool ShouldRefreshWsl(DateTimeOffset nextRefreshAt, DateTimeOffset now)
        => now >= nextRefreshAt;

    internal static DateTimeOffset NextWslRefreshAt(DateTimeOffset now, bool succeeded)
        => now + (succeeded ? WslCacheTtl : TimeSpan.FromSeconds(15));

    public static void RefreshWslProfiles()
    {
        lock (WslSync) _wslRefreshTask ??= Task.Run(RefreshWslAsync);
    }

    private static Task RefreshWslAsync()
    {
        string? executable = ResolveWslExecutable();
        WslRegistryQueryResult result = ReadWslDistributionsFromRegistry();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        bool changed = false;
        lock (WslSync)
        {
            // A transient registry access failure must not erase a previously useful list.
            if (result.Succeeded)
            {
                _wslSnapshot = new(executable, result.Distributions, result.DefaultDistribution,
                    NextWslRefreshAt(now, succeeded: true));
                changed = true;
            }
            else
            {
                _wslSnapshot = _wslSnapshot with
                {
                    Executable = executable ?? _wslSnapshot.Executable,
                    NextRefreshAt = NextWslRefreshAt(now, succeeded: false)
                };
            }
            _wslRefreshTask = null;
        }
        if (changed) NotifyProfilesChanged();
        return Task.CompletedTask;
    }

    internal static void NotifyProfilesChanged()
    {
        Delegate[] handlers = ProfilesChanged?.GetInvocationList() ?? [];
        foreach (EventHandler handler in handlers)
        {
            try { handler(null, EventArgs.Empty); }
            catch { /* One UI subscriber must not prevent the others from refreshing. */ }
        }
    }

    internal static IReadOnlyList<string> ParseWslDistributions(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return [];
        return output.Replace("\0", string.Empty, StringComparison.Ordinal).Replace("\uFEFF", string.Empty, StringComparison.Ordinal)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(name => name.Length > 0 && !name.Any(char.IsControl))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static WslRegistryQueryResult ReadWslDistributionsFromRegistry()
    {
        try
        {
            using RegistryKey? lxss = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss");
            if (lxss is null) return new(true, [], null);
            string? defaultKeyName = lxss.GetValue("DefaultDistribution") as string;
            var entries = new List<WslRegistryEntry>();
            foreach (string keyName in lxss.GetSubKeyNames())
            {
                using RegistryKey? distribution = lxss.OpenSubKey(keyName);
                entries.Add(new(keyName,
                    distribution?.GetValue("DistributionName"),
                    distribution?.GetValue("BasePath"),
                    distribution?.GetValue("Version")));
            }
            WslRegistryParseResult parsed = ParseWslRegistryEntries(entries, defaultKeyName, Directory.Exists);
            return new(true, parsed.Distributions, parsed.DefaultDistribution);
        }
        catch { return new(false, [], null); }
    }

    internal static WslRegistryParseResult ParseWslRegistryEntries(
        IEnumerable<WslRegistryEntry> entries, string? defaultKeyName, Func<string, bool> directoryExists)
    {
        var candidates = entries
            .Where(entry => Guid.TryParse(entry.KeyName, out _))
            .Where(entry => entry.DistributionName is string && entry.BasePath is string)
            .Where(entry => entry.Version is null || entry.Version is int version && version is 1 or 2)
            .Where(entry => IsExistingWslBasePath((string)entry.BasePath!, directoryExists))
            .Select(entry => new KeyValuePair<string?, string>(entry.KeyName, ((string)entry.DistributionName!).Trim()))
            .Where(entry => entry.Value.Length > 0 && !entry.Value.Any(char.IsControl))
            .Where(entry => !entry.Value.Equals("docker-desktop", StringComparison.OrdinalIgnoreCase) &&
                            !entry.Value.Equals("docker-desktop-data", StringComparison.OrdinalIgnoreCase))
            .GroupBy(entry => entry.Value, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        string? defaultDistribution = candidates.FirstOrDefault(entry =>
            string.Equals(entry.Key, defaultKeyName, StringComparison.OrdinalIgnoreCase)).Value;
        string[] distributions = candidates.OrderBy(entry =>
                !string.Equals(entry.Value, defaultDistribution, StringComparison.OrdinalIgnoreCase))
            .ThenBy(entry => entry.Value, StringComparer.OrdinalIgnoreCase)
            .Select(entry => entry.Value).ToArray();
        return new(distributions, defaultDistribution);
    }

    private static bool IsExistingWslBasePath(string basePath, Func<string, bool> directoryExists)
    {
        string expanded = Environment.ExpandEnvironmentVariables(basePath.Trim());
        if (expanded.Length == 0 || expanded.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;
        try { return Path.IsPathFullyQualified(expanded) && directoryExists(expanded); }
        catch { return false; }
    }

    private static string? ResolveWslExecutable()
    {
        string system = Path.Combine(Environment.SystemDirectory, "wsl.exe");
        return File.Exists(system) ? system : TryFindExecutable("wsl.exe");
    }

    internal static string BuildWslProfileId(string distribution)
        => "wsl-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(distribution)))[..12].ToLowerInvariant();

    internal static string BuildWslCommandLine(string executable, string distribution)
        => $"{QuoteCommand(executable)} --distribution {QuoteArgument(distribution)}";

    private static string QuoteArgument(string value)
    {
        var result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char ch in value)
        {
            if (ch == '\\') { slashes++; continue; }
            if (ch == '"') result.Append('\\', slashes * 2 + 1).Append(ch);
            else { result.Append('\\', slashes).Append(ch); }
            slashes = 0;
        }
        result.Append('\\', slashes * 2).Append('"');
        return result.ToString();
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
            (string executable, string[] arguments) = TerminalCommandLine.SplitCommandLine(commandLine);
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

    private sealed record WslRegistryQueryResult(bool Succeeded, IReadOnlyList<string> Distributions, string? DefaultDistribution);
    internal sealed record WslRegistryParseResult(IReadOnlyList<string> Distributions, string? DefaultDistribution);
    internal sealed record WslRegistryEntry(string? KeyName, object? DistributionName, object? BasePath, object? Version);
    private sealed record WslDiscoverySnapshot(string? Executable, IReadOnlyList<string> Distributions,
        string? DefaultDistribution, DateTimeOffset NextRefreshAt);
}
