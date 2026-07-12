using System.IO;

using Terminal.Sessions;
using Terminal.Settings;

namespace Terminal.Tests;

public sealed class TerminalProfileCatalogTests
{
    [Fact]
    public void ParseWslDistributionsHandlesUtf16NullsDuplicatesAndBlankLines()
    {
        IReadOnlyList<string> names = TerminalProfileCatalog.ParseWslDistributions(
            "\uFEFFUbuntu\0\r\n Debian \0\r\nubuntu\r\n\r\n");

        Assert.Equal(["Ubuntu", "Debian"], names);
    }

    [Fact]
    public void WslProfileIdentityAndCommandLineAreStableAndSafelyQuoted()
    {
        string first = TerminalProfileCatalog.BuildWslProfileId("Ubuntu 24.04");
        string second = TerminalProfileCatalog.BuildWslProfileId("Ubuntu 24.04");

        Assert.Equal(first, second);
        Assert.StartsWith("wsl-", first);
        Assert.Equal("\"C:\\Program Files\\WSL\\wsl.exe\" --distribution \"Ubuntu 24.04\"",
            TerminalProfileCatalog.BuildWslCommandLine("C:\\Program Files\\WSL\\wsl.exe", "Ubuntu 24.04"));
        Assert.EndsWith("--distribution \"quote\\\"and-path\\\\\"",
            TerminalProfileCatalog.BuildWslCommandLine("wsl.exe", "quote\"and-path\\"));
    }

    [Fact]
    public void ParseWslRegistryEntriesIgnoresInvalidValuesAndDeduplicatesNames()
    {
        TerminalProfileCatalog.WslRegistryEntry[] entries =
        [
            new("{11111111-1111-1111-1111-111111111111}", " Ubuntu ", @"C:\WSL\One", 2),
            new("{22222222-2222-2222-2222-222222222222}", "ubuntu", @"C:\WSL\Two", 2),
            new("{33333333-3333-3333-3333-333333333333}", null, @"C:\WSL\Three", 2),
            new("{44444444-4444-4444-4444-444444444444}", 42, @"C:\WSL\Four", 2),
            new("{55555555-5555-5555-5555-555555555555}", "bad\u0001name", @"C:\WSL\Five", 2),
            new("{66666666-6666-6666-6666-666666666666}", "Debian", @"C:\WSL\Six", 2),
            new("{77777777-7777-7777-7777-777777777777}", "DOCKER-DESKTOP", @"C:\WSL\Docker", 2),
            new("{88888888-8888-8888-8888-888888888888}", "docker-desktop-data", @"C:\WSL\DockerData", 2)
        ];

        TerminalProfileCatalog.WslRegistryParseResult result =
            TerminalProfileCatalog.ParseWslRegistryEntries(entries,
                "{66666666-6666-6666-6666-666666666666}", _ => true);
        Assert.Equal(["Debian", "Ubuntu"], result.Distributions);
        Assert.Equal("Debian", result.DefaultDistribution);
    }

    [Fact]
    public void ParseWslRegistryEntriesExcludesStaleAndMalformedRegistrations()
    {
        const string validKey = "{11111111-1111-1111-1111-111111111111}";
        const string missingKey = "{22222222-2222-2222-2222-222222222222}";
        TerminalProfileCatalog.WslRegistryEntry[] entries =
        [
            new(validKey, "Ubuntu", @"C:\WSL\Ubuntu", 2),
            new(missingKey, "Stale", @"C:\WSL\Missing", 2),
            new("not-a-guid", "Broken", @"C:\WSL\Broken", 2),
            new("{33333333-3333-3333-3333-333333333333}", "No path", null, 2),
            new("{44444444-4444-4444-4444-444444444444}", "Bad version", @"C:\WSL\Bad", 9),
            new("{55555555-5555-5555-5555-555555555555}", "docker-desktop", @"C:\WSL\Docker", 2)
        ];

        TerminalProfileCatalog.WslRegistryParseResult result = TerminalProfileCatalog.ParseWslRegistryEntries(
            entries, validKey, path => path.Equals(@"C:\WSL\Ubuntu", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(["Ubuntu"], result.Distributions);
        Assert.Equal("Ubuntu", result.DefaultDistribution);
    }

    [Fact]
    public void ParseWslRegistryEntriesClearsDefaultWhenItsRegistrationIsStale()
    {
        const string validKey = "{11111111-1111-1111-1111-111111111111}";
        const string staleDefaultKey = "{22222222-2222-2222-2222-222222222222}";
        TerminalProfileCatalog.WslRegistryParseResult result = TerminalProfileCatalog.ParseWslRegistryEntries(
            [
                new(validKey, "Ubuntu", @"C:\WSL\Ubuntu", 2),
                new(staleDefaultKey, "Stale", @"C:\WSL\Missing", 2)
            ], staleDefaultKey, path => path.EndsWith("Ubuntu", StringComparison.Ordinal));

        Assert.Equal(["Ubuntu"], result.Distributions);
        Assert.Null(result.DefaultDistribution);
    }

    [Fact]
    public void ProfilesChangedNotifiesRemainingSubscribersWhenOneThrows()
    {
        int called = 0;
        EventHandler broken = (_, _) => throw new InvalidOperationException();
        EventHandler healthy = (_, _) => called++;
        TerminalProfileCatalog.ProfilesChanged += broken;
        TerminalProfileCatalog.ProfilesChanged += healthy;
        try
        {
            TerminalProfileCatalog.NotifyProfilesChanged();
            Assert.Equal(1, called);
        }
        finally
        {
            TerminalProfileCatalog.ProfilesChanged -= broken;
            TerminalProfileCatalog.ProfilesChanged -= healthy;
        }
    }

    [Fact]
    public void WslCacheRefreshesExpiredMissingExecutableSnapshot()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Assert.True(TerminalProfileCatalog.ShouldRefreshWsl(DateTimeOffset.MinValue, now));
        Assert.False(TerminalProfileCatalog.ShouldRefreshWsl(now.AddSeconds(1), now));
        Assert.Equal(now.AddMinutes(5), TerminalProfileCatalog.NextWslRefreshAt(now, succeeded: true));
        Assert.Equal(now.AddSeconds(15), TerminalProfileCatalog.NextWslRefreshAt(now, succeeded: false));
    }
    [Fact]
    public void ResolveSelectedProfilePrefersMatchingCandidateOverStoredCustomId()
    {
        TerminalProfileDefinition candidateProfile = new(
            "pwsh",
            "PowerShell 7",
            "pwsh.exe -NoLogo",
            "PowerShell");
        TerminalProfileDefinition customProfile = new(
            "custom",
            "Custom",
            string.Empty,
            "Custom",
            IsCustom: true);

        TerminalProfileDefinition selectedProfile = TerminalProfileCatalog.ResolveSelectedProfile(
            [candidateProfile, customProfile],
            customProfile,
            profileId: "custom",
            commandLine: "pwsh.exe -NoLogo");

        Assert.Equal(candidateProfile, selectedProfile);
    }

    [Fact]
    public void ResolveSelectedProfileReturnsCustomWhenStoredCandidateNoLongerMatchesCommandLine()
    {
        TerminalProfileDefinition candidateProfile = new(
            "pwsh",
            "PowerShell 7",
            "pwsh.exe -NoLogo",
            "PowerShell");
        TerminalProfileDefinition customProfile = new(
            "custom",
            "Custom",
            string.Empty,
            "Custom",
            IsCustom: true);

        TerminalProfileDefinition selectedProfile = TerminalProfileCatalog.ResolveSelectedProfile(
            [candidateProfile, customProfile],
            customProfile,
            profileId: "pwsh",
            commandLine: "pwsh.exe -NoLogo -NoProfile");

        Assert.Equal(customProfile, selectedProfile);
    }

    [Fact]
    public void ResolveSelectedProfileMatchesEquivalentExecutablePathAndArguments()
    {
        TerminalProfileDefinition candidateProfile = new(
            "pwsh",
            "PowerShell 7",
            "pwsh.exe -NoLogo",
            "PowerShell");
        TerminalProfileDefinition customProfile = new(
            "custom",
            "Custom",
            string.Empty,
            "Custom",
            IsCustom: true);

        TerminalProfileDefinition selectedProfile = TerminalProfileCatalog.ResolveSelectedProfile(
            [candidateProfile, customProfile],
            customProfile,
            profileId: "custom",
            commandLine: "\"C:\\Users\\koya\\AppData\\Local\\Microsoft\\WindowsApps\\pwsh.exe\" -NoLogo");

        Assert.Equal(candidateProfile, selectedProfile);
    }

    [Fact]
    public void ResolveGitBashExecutablePrefersGitInstallOverWindowsSystemBash()
    {
        string rootDirectory = CreateTemporaryDirectory();
        try
        {
            string programFiles = Path.Combine(rootDirectory, "ProgramFiles");
            string programFilesX86 = Path.Combine(rootDirectory, "ProgramFilesX86");
            string pathDirectory = Path.Combine(rootDirectory, "PathBash");
            string gitBashPath = CreateExecutable(programFiles, "Git", "bin", "bash.exe");
            string systemBashPath = CreateExecutable(pathDirectory, "bash.exe");

            string? resolvedPath = TerminalProfileCatalog.ResolveGitBashExecutable(
                pathValue: pathDirectory,
                programFiles: programFiles,
                programFilesX86: programFilesX86);

            Assert.Equal(gitBashPath, resolvedPath);
            Assert.NotEqual(systemBashPath, resolvedPath);
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public void ResolveGitBashExecutableReturnsNullWhenOnlyWindowsSystemBashIsAvailable()
    {
        string? resolvedPath = TerminalProfileCatalog.ResolveGitBashExecutable(
            pathValue: Environment.SystemDirectory,
            programFiles: Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            programFilesX86: Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));

        Assert.Null(resolvedPath);
    }

    [Fact]
    public void ResolveGitBashExecutableFallsBackToPathWhenGitInstallIsMissing()
    {
        string rootDirectory = CreateTemporaryDirectory();
        try
        {
            string programFiles = Path.Combine(rootDirectory, "ProgramFiles");
            string programFilesX86 = Path.Combine(rootDirectory, "ProgramFilesX86");
            string pathDirectory = Path.Combine(rootDirectory, "CustomTools");
            string customBashPath = CreateExecutable(pathDirectory, "bash.exe");

            string? resolvedPath = TerminalProfileCatalog.ResolveGitBashExecutable(
                pathValue: pathDirectory,
                programFiles: programFiles,
                programFilesX86: programFilesX86);

            Assert.Equal(customBashPath, resolvedPath);
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Terminal.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string CreateExecutable(string parentDirectory, params string[] relativeSegments)
    {
        string fullPath = Path.Combine([parentDirectory, .. relativeSegments]);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, string.Empty);
        return fullPath;
    }
}
