using System.Text;

namespace ConPtyTerminal.Tests;

public sealed class SessionSmokeTests
{
    [Fact]
    public async Task ConPtySessionRunsInteractiveCommand()
    {
        await VerifyInteractiveEchoAsync(
            () => new ConPtySession(120, 30, BuildInteractiveCommandLine()),
            "conpty-smoke");
    }

    [Fact]
    public async Task ProcessPipeSessionRunsInteractiveCommand()
    {
        await VerifyInteractiveEchoAsync(
            () => new ProcessPipeSession(BuildInteractiveCommandLine()),
            "compat-smoke");
    }

    [Fact]
    public void ProcessPipeSessionSplitCommandLinePreservesQuotedArguments()
    {
        (string fileName, string[] arguments) = ProcessPipeSession.SplitCommandLine(
            "\"C:\\Program Files\\PowerShell\\7\\pwsh.exe\" -NoLogo -Command \"Write-Host hello world\"");

        Assert.Equal("C:\\Program Files\\PowerShell\\7\\pwsh.exe", fileName);
        Assert.Equal(
            ["-NoLogo", "-Command", "Write-Host hello world"],
            arguments);
    }

    private static async Task VerifyInteractiveEchoAsync(Func<ITerminalSession> sessionFactory, string expectedOutput)
    {
        using ITerminalSession session = sessionFactory();
        var output = new StringBuilder();
        var exitCodeSource = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        session.OutputReceived += (_, text) => output.Append(text);
        session.Exited += (_, code) => exitCodeSource.TrySetResult(code);
        session.Start();

        session.Write($"echo {expectedOutput}\r\n");
        session.Write("exit\r\n");

        int exitCode = await exitCodeSource.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(200);

        Assert.Equal(0, exitCode);
        Assert.Contains(expectedOutput, output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildInteractiveCommandLine()
    {
        string commandPath = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        return $"\"{commandPath}\" /Q /K";
    }
}
