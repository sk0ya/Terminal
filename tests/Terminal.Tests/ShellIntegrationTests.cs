using System.IO;

using Terminal.Sessions;

namespace Terminal.Tests;

public sealed class ShellIntegrationTests
{
    [Theory]
    [InlineData("pwsh.exe -NoLogo")]
    [InlineData("\"C:\\Program Files\\PowerShell\\7\\pwsh.exe\" -NoLogo")]
    [InlineData("pwsh")]
    public void PrepareLaunchInjectsIntegrationScriptForPwsh(string commandLine)
    {
        string scriptDirectory = CreateTempDirectory();
        try
        {
            string result = ShellIntegration.PrepareLaunch(commandLine, scriptDirectory);

            string scriptPath = Path.Combine(scriptDirectory, "shell-integration.ps1");
            Assert.StartsWith(commandLine, result);
            Assert.Contains("-NoExit -Command", result);
            Assert.Contains(scriptPath, result);
            Assert.True(File.Exists(scriptPath));
            Assert.Contains("]133;", File.ReadAllText(scriptPath));
        }
        finally
        {
            Directory.Delete(scriptDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData("pwsh.exe -Command Get-Date")]
    [InlineData("pwsh.exe -c Get-Date")]
    [InlineData("pwsh.exe -File script.ps1")]
    [InlineData("pwsh.exe -EncodedCommand ZQBjAGgAbwA=")]
    public void PrepareLaunchSkipsNonInteractivePwsh(string commandLine)
    {
        // Skipping happens before any script provisioning, so no directory is needed.
        string result = ShellIntegration.PrepareLaunch(commandLine, null);

        Assert.Equal(commandLine, result);
    }

    [Theory]
    [InlineData("powershell.exe -NoLogo")]
    [InlineData("cmd.exe /K")]
    [InlineData(@"C:\Program Files\Git\bin\bash.exe --login -i")]
    [InlineData("wsl.exe")]
    public void PrepareLaunchLeavesOtherShellsUnchanged(string commandLine)
    {
        string result = ShellIntegration.PrepareLaunch(commandLine, null);

        Assert.Equal(commandLine, result);
    }

    [Theory]
    [InlineData("pwsh.exe -NoLogo", true)]
    [InlineData("pwsh.exe -Command Get-Date", false)]
    [InlineData("powershell.exe -NoLogo", false)]
    [InlineData("cmd.exe /K", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void CanInjectReportsInjectableLaunches(string? commandLine, bool expected)
    {
        Assert.Equal(expected, ShellIntegration.CanInject(commandLine));
    }

    [Fact]
    public void AppendPowerShellInjectionEscapesSingleQuotesInScriptPath()
    {
        string result = ShellIntegration.AppendPowerShellInjection(
            "pwsh.exe",
            @"C:\It's Here\shell-integration.ps1");

        Assert.Contains(@"'C:\It''s Here\shell-integration.ps1'", result);
    }

    [Fact]
    public void EnsurePowerShellScriptRewritesStaleContent()
    {
        string scriptDirectory = CreateTempDirectory();
        try
        {
            string path = Path.Combine(scriptDirectory, "shell-integration.ps1");
            File.WriteAllText(path, "# old version");

            ShellIntegration.EnsurePowerShellScript(scriptDirectory);

            Assert.Equal(ShellIntegration.PowerShellScript, File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(scriptDirectory, recursive: true);
        }
    }

    [Fact]
    public void DefaultScriptPathPointsIntoLocalApplicationData()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.StartsWith(localAppData, ShellIntegration.DefaultScriptPath);
        Assert.EndsWith("shell-integration.ps1", ShellIntegration.DefaultScriptPath);
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "shellintegration-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
