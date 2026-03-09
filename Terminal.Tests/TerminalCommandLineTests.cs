using Terminal.Sessions;

namespace Terminal.Tests;

public sealed class TerminalCommandLineTests
{
    [Fact]
    public void SplitCommandLinePreservesQuotedArguments()
    {
        (string fileName, string[] arguments) = TerminalCommandLine.SplitCommandLine(
            "\"C:\\Program Files\\PowerShell\\7\\pwsh.exe\" -NoLogo -Command \"Write-Host hello world\"");

        Assert.Equal("C:\\Program Files\\PowerShell\\7\\pwsh.exe", fileName);
        Assert.Equal(
            ["-NoLogo", "-Command", "Write-Host hello world"],
            arguments);
    }
}
