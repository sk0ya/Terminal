using Terminal.Tabs;

namespace Terminal.Tests;

public sealed class TerminalHistorySeedResolverTests
{
    [Fact]
    public void ExistingReportedPathIsAuthoritativeForAnyShell()
    {
        int defaultProbeCount = 0;

        string? result = TerminalHistorySeedResolver.ResolvePath(
            @"C:\custom\history.txt",
            "cmd.exe",
            path => path == @"C:\custom\history.txt",
            () =>
            {
                defaultProbeCount++;
                return @"C:\default\history.txt";
            });

        Assert.Equal(@"C:\custom\history.txt", result);
        Assert.Equal(0, defaultProbeCount);
    }

    [Theory]
    [InlineData("pwsh")]
    [InlineData("pwsh.exe -NoLogo")]
    [InlineData("powershell.EXE -NoProfile")]
    [InlineData("\"C:\\Program Files\\PowerShell\\7\\pwsh.exe\" -NoLogo")]
    public void PowerShellCommandFallsBackToDefaultHistoryPath(string commandLine)
    {
        string? result = TerminalHistorySeedResolver.ResolvePath(
            reportedHistoryPath: null,
            commandLine,
            _ => false,
            () => @"C:\default\history.txt");

        Assert.Equal(@"C:\default\history.txt", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("cmd.exe")]
    [InlineData("bash -l")]
    public void NonPowerShellCommandDoesNotProbeDefaultHistoryPath(string? commandLine)
    {
        int defaultProbeCount = 0;

        string? result = TerminalHistorySeedResolver.ResolvePath(
            @"C:\missing\history.txt",
            commandLine,
            _ => false,
            () =>
            {
                defaultProbeCount++;
                return @"C:\default\history.txt";
            });

        Assert.Null(result);
        Assert.Equal(0, defaultProbeCount);
    }
}
