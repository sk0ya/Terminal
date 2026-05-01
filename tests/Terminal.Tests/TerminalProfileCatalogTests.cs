using System.IO;

using Terminal.Sessions;
using Terminal.Settings;

namespace Terminal.Tests;

public sealed class TerminalProfileCatalogTests
{
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
