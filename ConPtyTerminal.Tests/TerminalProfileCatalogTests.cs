using System.IO;

namespace ConPtyTerminal.Tests;

public sealed class TerminalProfileCatalogTests
{
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
        string directory = Path.Combine(Path.GetTempPath(), "ConPtyTerminal.Tests", Guid.NewGuid().ToString("N"));
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
