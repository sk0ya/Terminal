using System.Runtime.InteropServices;

namespace Terminal.Sessions;

internal sealed class ConPtyProcessLifetime
{
    private const uint WaitTimeout = 0x00000102;
    private readonly IConPtyProcessLifetimeApi _api;
    private readonly IntPtr _process;
    private readonly IntPtr _job;
    private readonly CancellationTokenSource _monitorCancellation = new();
    private Task? _monitorTask;
    private int _exitReported;

    internal ConPtyProcessLifetime(IConPtyProcessLifetimeApi api, IntPtr process, IntPtr job)
    {
        _api = api;
        _process = process;
        _job = job;
    }

    internal bool IsRunning => _process != IntPtr.Zero && _api.Wait(_process, 0) == WaitTimeout;

    internal void StartMonitoring(Action<int> exited)
    {
        _monitorTask ??= Task.Run(async () =>
        {
            while (!_monitorCancellation.IsCancellationRequested)
            {
                uint wait = _api.Wait(_process, 100);
                if (wait == WaitTimeout) { await Task.Yield(); continue; }
                if (wait == 0 && _api.TryGetExitCode(_process, out uint code)
                    && Interlocked.Exchange(ref _exitReported, 1) == 0)
                {
                    exited(unchecked((int)code));
                }
                return;
            }
        });
    }

    internal bool TryTerminate(uint exitCode)
        => IsRunning && (_job != IntPtr.Zero ? _api.TerminateJob(_job, exitCode) : _api.TerminateProcess(_process, exitCode));

    internal async Task ShutdownAsync(ConPtyOwnedHandles? handles, Action<IntPtr> closePseudoConsole)
    {
        _monitorCancellation.Cancel();
        handles?.CloseCommunicationHandles(closePseudoConsole);
        await WaitTaskAsync(_monitorTask, TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);

        if (_process != IntPtr.Zero && !await WaitForExitAsync(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false))
        {
            _ = _job != IntPtr.Zero ? _api.TerminateJob(_job, 1) : _api.TerminateProcess(_process, 1);
            _ = await WaitForExitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
        }

        handles?.CloseProcessHandles(_api.CloseHandle);
        _monitorCancellation.Dispose();
    }

    private async Task<bool> WaitForExitAsync(TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            uint wait = _api.Wait(_process, 50);
            if (wait == 0) return true;
            if (wait != WaitTimeout) return false;
            await Task.Delay(25).ConfigureAwait(false);
        }
        return _api.Wait(_process, 0) == 0;
    }

    private static async Task WaitTaskAsync(Task? task, TimeSpan timeout)
    {
        if (task is null) return;
        try { await task.WaitAsync(timeout).ConfigureAwait(false); } catch { }
    }
}

internal interface IConPtyProcessLifetimeApi
{
    uint Wait(IntPtr process, uint milliseconds);
    bool TryGetExitCode(IntPtr process, out uint exitCode);
    bool TerminateProcess(IntPtr process, uint exitCode);
    bool TerminateJob(IntPtr job, uint exitCode);
    void CloseHandle(IntPtr handle);
}

internal sealed class WindowsConPtyProcessLifetimeApi : IConPtyProcessLifetimeApi
{
    internal static WindowsConPtyProcessLifetimeApi Instance { get; } = new();
    public uint Wait(IntPtr process, uint milliseconds) => WaitForSingleObject(process, milliseconds);
    public bool TryGetExitCode(IntPtr process, out uint exitCode) => GetExitCodeProcess(process, out exitCode);
    public bool TerminateProcess(IntPtr process, uint exitCode) => TerminateProcessNative(process, exitCode);
    public bool TerminateJob(IntPtr job, uint exitCode) => TerminateJobObject(job, exitCode);
    public void CloseHandle(IntPtr handle) => _ = CloseHandleNative(handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "TerminateProcess")] private static extern bool TerminateProcessNative(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(IntPtr job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "CloseHandle")] private static extern bool CloseHandleNative(IntPtr handle);
}
