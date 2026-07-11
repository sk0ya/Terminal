using System.IO;

using Terminal.Sessions;

namespace Terminal.Tests;

public sealed class ShellIntegrationTests
{
    [Theory]
    [InlineData("powershell.exe -NoLogo")]
    public void PrepareLaunchInjectsAdditionalInteractiveShells(string commandLine)
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            string result = ShellIntegration.PrepareLaunch(commandLine, directory);
            Assert.NotEqual(commandLine, result);
            Assert.Contains("-NoExit -Command", result);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("powershell.exe -Command Get-Date")]
    [InlineData("bash.exe -c ls")]
    [InlineData("bash.exe script.sh")]
    public void PrepareLaunchDoesNotAlterAdditionalNonInteractiveShells(string commandLine)
        => Assert.Equal(commandLine, ShellIntegration.PrepareLaunch(commandLine, null));

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
    [InlineData("powershell.exe -Com Get-Date")]
    [InlineData("powershell.exe -Fi script.ps1")]
    [InlineData("powershell.exe -Encoded ZQBjAGgAbwA=")]
    [InlineData("powershell.exe -UnknownSwitch")]
    public void PrepareLaunchSkipsNonInteractivePwsh(string commandLine)
    {
        // Skipping happens before any script provisioning, so no directory is needed.
        string result = ShellIntegration.PrepareLaunch(commandLine, null);

        Assert.Equal(commandLine, result);
    }

    [Theory]
    [InlineData("cmd.exe /K")]
    [InlineData("wsl.exe")]
    public void PrepareLaunchLeavesOtherShellsUnchanged(string commandLine)
    {
        string result = ShellIntegration.PrepareLaunch(commandLine, null);

        Assert.Equal(commandLine, result);
    }

    [Theory]
    [InlineData("pwsh.exe -NoLogo", true)]
    [InlineData("pwsh.exe -Command Get-Date", false)]
    [InlineData("powershell.exe -NoLogo", true)]
    [InlineData("bash.exe --login -i", false)]
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
    public async Task EnsurePowerShellScriptConcurrentlyConvergesOnCompleteContent()
    {
        string scriptDirectory = CreateTempDirectory();
        try
        {
            Task<string>[] writes = Enumerable.Range(0, 16)
                .Select(_ => Task.Run(() => ShellIntegration.EnsurePowerShellScript(scriptDirectory)))
                .ToArray();
            string[] paths = await Task.WhenAll(writes);

            Assert.Single(paths.Distinct(StringComparer.OrdinalIgnoreCase));
            Assert.Equal(ShellIntegration.PowerShellScript, File.ReadAllText(paths[0]));
            Assert.Empty(Directory.EnumerateFiles(scriptDirectory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(scriptDirectory, recursive: true);
        }
    }

    [Fact]
    public void PowerShellScriptEmitsCommandLineMarker()
    {
        // The ReadLine hook must report the command via OSC 633;E before 133;C
        // so the host can build a command history.
        Assert.Contains("]633;E;", ShellIntegration.PowerShellScript);
        Assert.Contains("__ConPtyTerminalEncodeCommandLine", ShellIntegration.PowerShellScript);
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
