using Microsoft.Win32.SafeHandles;
using System.IO;
using System.Runtime.InteropServices;

namespace Terminal.Sessions;

public sealed class ConPtySession : ITerminalSession
{
    private readonly object _syncRoot = new();
    private readonly string? _workingDirectory;
    private readonly IReadOnlyDictionary<string, string?>? _environmentVariables;
    private readonly ConPtyHandleOwner _handles = new();
    private int _processId;
    private ConPtyIoPump? _ioPump;
    private ConPtyProcessLifetime? _processLifetime;
    private bool _started;
    private DateTime _startedAtUtc;
    private DateTime _lastOutputAtUtc;
    private bool _hasOutput;
    private bool _disposed;

    public TerminalSessionCapabilities Capabilities { get; } = new(
        TerminalSessionKind.ConPty,
        SupportsResize: true,
        SupportsTerminalInput: true);

    internal int ProcessId => _processId;

    public event EventHandler<string>? OutputReceived;
    public event EventHandler<int>? Exited;

    public ConPtySession(
        short columns,
        short rows,
        string commandLine,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string?>? environmentVariables = null)
    {
        if (columns <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(columns));
        }

        if (rows <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rows));
        }

        if (string.IsNullOrWhiteSpace(commandLine))
        {
            throw new ArgumentException("Command line is required.", nameof(commandLine));
        }

        if (!string.IsNullOrWhiteSpace(workingDirectory) && !Directory.Exists(workingDirectory))
        {
            throw new DirectoryNotFoundException($"Working directory was not found: {workingDirectory}");
        }

        _workingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory;
        _environmentVariables = environmentVariables is null
            ? null
            : new Dictionary<string, string?>(environmentVariables, StringComparer.OrdinalIgnoreCase);

        try
        {
            new ConPtyPseudoConsoleFactory(WindowsConPtyPseudoConsoleApi.Instance)
                .Create(columns, rows, _handles);
            _processId = new ConPtyProcessLauncher(WindowsConPtyProcessApi.Instance)
                .Launch(commandLine, _workingDirectory, _environmentVariables, _handles);
            _processLifetime = new ConPtyProcessLifetime(
                WindowsConPtyProcessLifetimeApi.Instance,
                _handles.Process,
                _handles.Job);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_syncRoot)
        {
            if (_started)
            {
                return;
            }

            _started = true;
            _startedAtUtc = DateTime.UtcNow;
            _lastOutputAtUtc = _startedAtUtc;
            _hasOutput = false;
        }

        StartOutputReadLoop();
        _processLifetime!.StartMonitoring(exitCode =>
        {
            if (!_disposed) Exited?.Invoke(this, exitCode);
        });
    }

    public void Write(string input)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_syncRoot)
        {
            _ioPump?.Write(input);
        }
    }

    public void Write(byte[] input)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (input.Length == 0)
        {
            return;
        }

        lock (_syncRoot)
        {
            _ioPump?.Write(input);
        }
    }

    public void Resize(short columns, short rows)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int hr = ResizePseudoConsole(_handles.PseudoConsole, new Coord(columns, rows));
        if (hr != 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }
    }

    public bool IsOutputStalled(TimeSpan initialOutputTimeout, TimeSpan idleOutputTimeout)
    {
        _ = idleOutputTimeout;

        if (_disposed || !_started || _processLifetime is null)
        {
            return false;
        }

        if (!_processLifetime.IsRunning)
        {
            return false;
        }

        return TerminalSessionStallDetector.IsStartupStalled(
            _hasOutput,
            _startedAtUtc,
            DateTime.UtcNow,
            initialOutputTimeout);
    }

    public bool TryForceUnlock(uint exitCode = 1)
    {
        return !_disposed && _processLifetime?.TryTerminate(exitCode) == true;
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        ConPtyIoPump? ioPump;
        ConPtyProcessLifetime? processLifetime;
        ConPtyOwnedHandles? handles;

        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            ioPump = _ioPump;
            processLifetime = _processLifetime;
            handles = _handles.DetachForShutdown();

            _ioPump = null;
            _processLifetime = null;
        }

        ioPump?.TryRequestShellExit();
        if (ioPump is not null)
        {
            await ioPump.DisposeAsync().ConfigureAwait(false);
        }
        if (processLifetime is not null)
        {
            await processLifetime.ShutdownAsync(handles, ClosePseudoConsoleHandle).ConfigureAwait(false);
        }
        else
        {
            handles?.CloseCommunicationHandles(ClosePseudoConsoleHandle);
            handles?.CloseProcessHandles(WindowsConPtyProcessLifetimeApi.Instance.CloseHandle);
        }

        GC.SuppressFinalize(this);
    }

    private void StartOutputReadLoop()
    {
        if (_handles.InputWrite is null || _handles.OutputRead is null)
        {
            throw new InvalidOperationException("ConPTY pipes are not initialized.");
        }

        _ioPump = new ConPtyIoPump(_handles.InputWrite, _handles.OutputRead);
        _ = _ioPump.Start(output =>
        {
            _hasOutput = true;
            _lastOutputAtUtc = DateTime.UtcNow;
            OutputReceived?.Invoke(this, output);
        });
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(
        IntPtr hPC,
        Coord size);

    [DllImport("kernel32.dll", SetLastError = false, EntryPoint = "ClosePseudoConsole")]
    private static extern void ClosePseudoConsoleHandle(IntPtr hPC);

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        public short X;
        public short Y;

        public Coord(short x, short y)
        {
            X = x;
            Y = y;
        }
    }

}
