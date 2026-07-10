using Terminal.Sessions;

namespace Terminal.Tests;

public sealed class ConPtyProcessLifetimeTests
{
    [Fact]
    public async Task MonitorReportsExitOnlyOnce()
    {
        var api = new FakeApi { WaitResult = 0, ExitCode = 23 };
        var lifetime = new ConPtyProcessLifetime(api, (IntPtr)1, IntPtr.Zero);
        var exits = new List<int>();
        lifetime.StartMonitoring(exits.Add);
        lifetime.StartMonitoring(exits.Add);
        await Task.Delay(50);
        await lifetime.ShutdownAsync(null, _ => { });
        Assert.Equal([23], exits);
    }

    [Fact]
    public void ForceTerminationUsesJobWhenPresent()
    {
        var api = new FakeApi { WaitResult = 0x102 };
        var lifetime = new ConPtyProcessLifetime(api, (IntPtr)1, (IntPtr)2);
        Assert.True(lifetime.TryTerminate(9));
        Assert.Equal([((IntPtr)2, 9u)], api.TerminatedJobs);
        Assert.Empty(api.TerminatedProcesses);
    }

    private sealed class FakeApi : IConPtyProcessLifetimeApi
    {
        public uint WaitResult { get; init; }
        public uint ExitCode { get; init; }
        public List<(IntPtr, uint)> TerminatedJobs { get; } = [];
        public List<(IntPtr, uint)> TerminatedProcesses { get; } = [];
        public uint Wait(IntPtr process, uint milliseconds) => WaitResult;
        public bool TryGetExitCode(IntPtr process, out uint exitCode) { exitCode = ExitCode; return true; }
        public bool TerminateProcess(IntPtr process, uint exitCode) { TerminatedProcesses.Add((process, exitCode)); return true; }
        public bool TerminateJob(IntPtr job, uint exitCode) { TerminatedJobs.Add((job, exitCode)); return true; }
        public void CloseHandle(IntPtr handle) { }
    }
}
