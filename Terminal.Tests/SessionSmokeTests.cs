using System.Diagnostics;
using System.IO;
using System.Text;

using Terminal.Sessions;

namespace Terminal.Tests;

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
    public async Task ConPtySessionHonorsWorkingDirectory()
    {
        string workingDirectory = CreateTemporaryWorkingDirectory();
        try
        {
            await VerifyOneShotOutputAsync(
                () => new ConPtySession(120, 30, BuildWorkingDirectoryCommandLine(), workingDirectory),
                workingDirectory);
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
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
    public async Task ConPtySessionDisposeAsyncExitsTrackedProcess()
    {
        var session = new ConPtySession(120, 30, BuildInteractiveCommandLine());
        int processId = session.ProcessId;
        session.Start();

        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForProcessExitAsync(processId, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ConPtySessionTryForceUnlockStopsNestedBashSession()
    {
        var session = new ConPtySession(120, 30, BuildInteractiveCommandLine());
        int processId = session.ProcessId;

        session.Start();
        session.Write("bash\r\n");
        await Task.Delay(1500);

        Assert.True(session.TryForceUnlock());
        await WaitForProcessExitAsync(processId, TimeSpan.FromSeconds(5));
        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
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

    private static async Task VerifyOneShotOutputAsync(Func<ITerminalSession> sessionFactory, string expectedOutput)
    {
        using ITerminalSession session = sessionFactory();
        var output = new StringBuilder();
        var exitCodeSource = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        session.OutputReceived += (_, text) => output.Append(text);
        session.Exited += (_, code) => exitCodeSource.TrySetResult(code);
        session.Start();

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

    private static string BuildWorkingDirectoryCommandLine()
    {
        string commandPath = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        return $"\"{commandPath}\" /C cd";
    }

    private static string CreateTemporaryWorkingDirectory()
    {
        string workingDirectory = Path.Combine(Path.GetTempPath(), "Terminal.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        return workingDirectory;
    }

    private static async Task WaitForProcessExitAsync(int processId, TimeSpan timeout)
    {
        DateTime deadlineUtc = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadlineUtc)
        {
            if (!IsProcessRunning(processId))
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.False(IsProcessRunning(processId), $"Process {processId} was still running after {timeout}.");
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }
}
