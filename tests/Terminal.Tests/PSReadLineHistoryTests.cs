using System.IO;

using Terminal.Sessions;

namespace Terminal.Tests;

public sealed class PSReadLineHistoryTests
{
    [Fact]
    public void ParseReturnsEachLineAsCommandOldestFirst()
    {
        IReadOnlyList<string> commands = PSReadLineHistory.Parse(
        [
            "git status",
            "dotnet build",
            "ls"
        ]);

        Assert.Equal(["git status", "dotnet build", "ls"], commands);
    }

    [Fact]
    public void ParseJoinsBacktickContinuationsIntoOneCommand()
    {
        // A trailing backtick marks a multi-line command continued on the next line.
        IReadOnlyList<string> commands = PSReadLineHistory.Parse(
        [
            "foreach ($x in 1..3) {`",
            "    Write-Host $x`",
            "}"
        ]);

        Assert.Equal(["foreach ($x in 1..3) {\n    Write-Host $x\n}"], commands);
    }

    [Fact]
    public void ParseTreatsEvenTrailingBackticksAsLiteral()
    {
        // Two trailing backticks are a literal (escaped) backtick, not a continuation.
        IReadOnlyList<string> commands = PSReadLineHistory.Parse(
        [
            "echo ``",
            "next"
        ]);

        Assert.Equal(["echo ``", "next"], commands);
    }

    [Fact]
    public void ParseEmitsPartialCommandWhenFileEndsMidContinuation()
    {
        IReadOnlyList<string> commands = PSReadLineHistory.Parse(
        [
            "first line`"
        ]);

        Assert.Equal(["first line\n"], commands);
    }

    [Fact]
    public void ReadReturnsEmptyForMissingFile()
    {
        string missing = Path.Combine(Path.GetTempPath(), "no-such-history-" + Guid.NewGuid().ToString("N") + ".txt");

        Assert.Empty(PSReadLineHistory.Read(missing));
    }

    [Fact]
    public void DefaultHistoryPathsCoverBothKnownLocations()
    {
        IReadOnlyList<string> paths = PSReadLineHistory.DefaultHistoryPaths;

        Assert.Equal(2, paths.Count);
        // The Windows\PowerShell location is PSReadLine's real default for both
        // editions, so it must be probed first.
        Assert.EndsWith(Path.Combine("Microsoft", "Windows", "PowerShell", "PSReadLine", "ConsoleHost_history.txt"), paths[0]);
        Assert.EndsWith(Path.Combine("Microsoft", "PowerShell", "PSReadLine", "ConsoleHost_history.txt"), paths[1]);
    }
}
