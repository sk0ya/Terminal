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

    [Fact]
    public async Task ProcessPipeSessionPublishesCompatibilityEnvironment()
    {
        using ITerminalSession session = new ProcessPipeSession(
            BuildCompatibilityEnvironmentCommandLine(),
            columns: 132,
            rows: 41);
        var output = new StringBuilder();
        var exitCodeSource = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        session.OutputReceived += (_, text) => output.Append(text);
        session.Exited += (_, code) => exitCodeSource.TrySetResult(code);
        session.Start();

        int exitCode = await exitCodeSource.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, exitCode);
        Assert.Contains("TERM=dumb", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("COLUMNS=132", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LINES=41", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConPtySessionDisposeAsyncStopsRunningSession()
    {
        ITerminalSession session = new ConPtySession(120, 30, BuildInteractiveCommandLine());
        session.Start();

        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ProcessPipeSessionDisposeAsyncStopsRunningSession()
    {
        ITerminalSession session = new ProcessPipeSession(BuildInteractiveCommandLine());
        session.Start();

        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
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

    private static string BuildCompatibilityEnvironmentCommandLine()
    {
        string commandPath = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        return $"\"{commandPath}\" /C echo TERM=%TERM% COLUMNS=%COLUMNS% LINES=%LINES%";
    }
}
