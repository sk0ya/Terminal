using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ConPtyTerminal;

public sealed class ConPtySession : ITerminalSession
{
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint HandleFlagInherit = 0x00000001;
    private const int StartfUseStdHandles = 0x00000100;
    private const int ProcThreadAttributePseudoConsole = 0x00020016;
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const uint WaitTimeout = 0x00000102;

    private readonly object _syncRoot = new();
    private readonly string? _workingDirectory;
    private IntPtr _pseudoConsole;
    private IntPtr _processHandle;
    private IntPtr _threadHandle;
    private IntPtr _jobHandle;
    private int _processId;
    private SafeFileHandle? _pseudoConsoleInputReadHandle;
    private SafeFileHandle? _pseudoConsoleOutputWriteHandle;
    private SafeFileHandle? _inputWriteHandle;
    private SafeFileHandle? _outputReadHandle;
    private Stream? _inputStream;
    private StreamWriter? _inputWriter;
    private StreamReader? _outputReader;
    private CancellationTokenSource? _readCancellation;
    private Task? _readTask;
    private CancellationTokenSource? _exitMonitorCancellation;
    private Task? _exitMonitorTask;
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

    public ConPtySession(short columns, short rows, string commandLine, string? workingDirectory = null)
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

        try
        {
            CreatePseudoConsole(columns, rows);
            LaunchProcess(commandLine);
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
        StartExitMonitor();
    }

    public void Write(string input)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_syncRoot)
        {
            _inputWriter?.Write(input);
            _inputWriter?.Flush();
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
            _inputWriter?.Flush();
            _inputStream?.Write(input, 0, input.Length);
            _inputStream?.Flush();
        }
    }

    public void Resize(short columns, short rows)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int hr = ResizePseudoConsole(_pseudoConsole, new Coord(columns, rows));
        if (hr != 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }
    }

    public bool IsOutputStalled(TimeSpan initialOutputTimeout, TimeSpan idleOutputTimeout)
    {
        _ = idleOutputTimeout;

        if (_disposed || !_started || _processHandle == IntPtr.Zero)
        {
            return false;
        }

        if (WaitForSingleObject(_processHandle, 0) != WaitTimeout)
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
        if (_disposed || _processHandle == IntPtr.Zero)
        {
            return false;
        }

        if (WaitForSingleObject(_processHandle, 0) != WaitTimeout)
        {
            return false;
        }

        if (_jobHandle != IntPtr.Zero)
        {
            return TerminateJobObject(_jobHandle, exitCode);
        }

        return TerminateProcess(_processHandle, exitCode);
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        StreamWriter? inputWriter;
        Stream? inputStream;
        StreamReader? outputReader;
        CancellationTokenSource? readCancellation;
        Task? readTask;
        CancellationTokenSource? exitMonitorCancellation;
        Task? exitMonitorTask;
        SafeFileHandle? pseudoConsoleInputReadHandle;
        SafeFileHandle? pseudoConsoleOutputWriteHandle;
        SafeFileHandle? inputWriteHandle;
        SafeFileHandle? outputReadHandle;
        IntPtr pseudoConsole;
        IntPtr processHandle;
        IntPtr threadHandle;
        IntPtr jobHandle;

        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            inputWriter = _inputWriter;
            inputStream = _inputStream;
            outputReader = _outputReader;
            readCancellation = _readCancellation;
            readTask = _readTask;
            exitMonitorCancellation = _exitMonitorCancellation;
            exitMonitorTask = _exitMonitorTask;
            pseudoConsoleInputReadHandle = _pseudoConsoleInputReadHandle;
            pseudoConsoleOutputWriteHandle = _pseudoConsoleOutputWriteHandle;
            inputWriteHandle = _inputWriteHandle;
            outputReadHandle = _outputReadHandle;
            pseudoConsole = _pseudoConsole;
            processHandle = _processHandle;
            threadHandle = _threadHandle;
            jobHandle = _jobHandle;

            _inputWriter = null;
            _inputStream = null;
            _outputReader = null;
            _readCancellation = null;
            _readTask = null;
            _exitMonitorCancellation = null;
            _exitMonitorTask = null;
            _pseudoConsoleInputReadHandle = null;
            _pseudoConsoleOutputWriteHandle = null;
            _inputWriteHandle = null;
            _outputReadHandle = null;
            _pseudoConsole = IntPtr.Zero;
            _processHandle = IntPtr.Zero;
            _threadHandle = IntPtr.Zero;
            _jobHandle = IntPtr.Zero;
        }

        TryWriteExit(inputWriter);

        readCancellation?.Cancel();
        exitMonitorCancellation?.Cancel();

        DisposeQuietly(inputWriter);
        DisposeQuietly(inputStream);
        DisposeQuietly(outputReader);
        DisposeQuietly(pseudoConsoleInputReadHandle);
        DisposeQuietly(pseudoConsoleOutputWriteHandle);
        DisposeQuietly(inputWriteHandle);
        DisposeQuietly(outputReadHandle);

        if (pseudoConsole != IntPtr.Zero)
        {
            ClosePseudoConsoleHandle(pseudoConsole);
        }

        await WaitForTaskAsync(readTask, TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
        await WaitForTaskAsync(exitMonitorTask, TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);

        if (processHandle != IntPtr.Zero && !await WaitForProcessExitAsync(processHandle, TimeSpan.FromMilliseconds(250)).ConfigureAwait(false))
        {
            if (jobHandle != IntPtr.Zero)
            {
                _ = TerminateJobObject(jobHandle, 1);
            }
            else
            {
                _ = TerminateProcess(processHandle, 1);
            }

            _ = await WaitForProcessExitAsync(processHandle, TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
        }

        await WaitForTaskAsync(readTask, TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
        await WaitForTaskAsync(exitMonitorTask, TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);

        if (threadHandle != IntPtr.Zero)
        {
            CloseHandle(threadHandle);
        }

        if (processHandle != IntPtr.Zero)
        {
            CloseHandle(processHandle);
        }

        if (jobHandle != IntPtr.Zero)
        {
            CloseHandle(jobHandle);
        }

        DisposeQuietly(readCancellation);
        DisposeQuietly(exitMonitorCancellation);

        GC.SuppressFinalize(this);
    }

    private void CreatePseudoConsole(short columns, short rows)
    {
        IntPtr pipeToPseudoConsoleInputRead = IntPtr.Zero;
        IntPtr pipeToPseudoConsoleInputWrite = IntPtr.Zero;
        IntPtr pipeFromPseudoConsoleOutputRead = IntPtr.Zero;
        IntPtr pipeFromPseudoConsoleOutputWrite = IntPtr.Zero;
        var pipeSecurity = new SecurityAttributes
        {
            nLength = Marshal.SizeOf<SecurityAttributes>(),
            bInheritHandle = true
        };

        try
        {
            if (!CreatePipe(
                    out pipeToPseudoConsoleInputRead,
                    out pipeToPseudoConsoleInputWrite,
                    ref pipeSecurity,
                    0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create ConPTY input pipe.");
            }

            if (!CreatePipe(
                    out pipeFromPseudoConsoleOutputRead,
                    out pipeFromPseudoConsoleOutputWrite,
                    ref pipeSecurity,
                    0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create ConPTY output pipe.");
            }

            if (!SetHandleInformation(pipeToPseudoConsoleInputWrite, HandleFlagInherit, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to configure ConPTY input pipe.");
            }

            if (!SetHandleInformation(pipeFromPseudoConsoleOutputRead, HandleFlagInherit, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to configure ConPTY output pipe.");
            }

            int hr = CreatePseudoConsoleHandle(
                new Coord(columns, rows),
                pipeToPseudoConsoleInputRead,
                pipeFromPseudoConsoleOutputWrite,
                0,
                out _pseudoConsole);

            if (hr != 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            _inputWriteHandle = new SafeFileHandle(pipeToPseudoConsoleInputWrite, ownsHandle: true);
            _outputReadHandle = new SafeFileHandle(pipeFromPseudoConsoleOutputRead, ownsHandle: true);
            _pseudoConsoleInputReadHandle = new SafeFileHandle(pipeToPseudoConsoleInputRead, ownsHandle: true);
            _pseudoConsoleOutputWriteHandle = new SafeFileHandle(pipeFromPseudoConsoleOutputWrite, ownsHandle: true);

            pipeToPseudoConsoleInputWrite = IntPtr.Zero;
            pipeFromPseudoConsoleOutputRead = IntPtr.Zero;
            pipeToPseudoConsoleInputRead = IntPtr.Zero;
            pipeFromPseudoConsoleOutputWrite = IntPtr.Zero;
        }
        finally
        {
            if (pipeToPseudoConsoleInputRead != IntPtr.Zero)
            {
                CloseHandle(pipeToPseudoConsoleInputRead);
            }

            if (pipeFromPseudoConsoleOutputWrite != IntPtr.Zero)
            {
                CloseHandle(pipeFromPseudoConsoleOutputWrite);
            }

            if (pipeToPseudoConsoleInputWrite != IntPtr.Zero)
            {
                CloseHandle(pipeToPseudoConsoleInputWrite);
            }

            if (pipeFromPseudoConsoleOutputRead != IntPtr.Zero)
            {
                CloseHandle(pipeFromPseudoConsoleOutputRead);
            }
        }
    }

    private void LaunchProcess(string commandLine)
    {
        IntPtr attributeList = IntPtr.Zero;
        IntPtr attributeListSize = IntPtr.Zero;
        _ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize);

        if (attributeListSize == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to get attribute list size.");
        }

        try
        {
            attributeList = Marshal.AllocHGlobal(attributeListSize);

            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to initialize attribute list.");
            }

            IntPtr pseudoConsoleValue = _pseudoConsole;

            if (!UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    (IntPtr)ProcThreadAttributePseudoConsole,
                    pseudoConsoleValue,
                    (IntPtr)IntPtr.Size,
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to update pseudo console attribute.");
            }

            var startupInfo = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    cb = Marshal.SizeOf<StartupInfoEx>(),
                    dwFlags = StartfUseStdHandles,
                    hStdInput = IntPtr.Zero,
                    hStdOutput = IntPtr.Zero,
                    hStdError = IntPtr.Zero
                },
                lpAttributeList = attributeList
            };
            var commandLineBuffer = new StringBuilder(commandLine);

            if (!CreateProcess(
                    null,
                    commandLineBuffer,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    ExtendedStartupInfoPresent,
                    IntPtr.Zero,
                    _workingDirectory,
                    ref startupInfo,
                    out ProcessInformation processInfo))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Failed to launch command: {commandLine}");
            }

            _processHandle = processInfo.hProcess;
            _threadHandle = processInfo.hThread;
            _processId = processInfo.dwProcessId;
            ConfigureProcessJob(_processHandle);

            _pseudoConsoleInputReadHandle?.Dispose();
            _pseudoConsoleInputReadHandle = null;
            _pseudoConsoleOutputWriteHandle?.Dispose();
            _pseudoConsoleOutputWriteHandle = null;
        }
        finally
        {
            if (attributeList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }
        }
    }

    private void StartOutputReadLoop()
    {
        if (_inputWriteHandle is null || _outputReadHandle is null)
        {
            throw new InvalidOperationException("ConPTY pipes are not initialized.");
        }

        _inputStream = new FileStream(_inputWriteHandle, FileAccess.Write, 4096, isAsync: false);
        _inputWriter = new StreamWriter(
            _inputStream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 4096,
            leaveOpen: true)
        {
            AutoFlush = true
        };

        _outputReader = new StreamReader(
            new FileStream(_outputReadHandle, FileAccess.Read, 4096, isAsync: false),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        _readCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _readCancellation.Token;

        _readTask = Task.Run(() =>
        {
            char[] buffer = new char[4096];

            while (!cancellationToken.IsCancellationRequested)
            {
                int read;
                try
                {
                    read = _outputReader.Read(buffer, 0, buffer.Length);
                }
                catch
                {
                    break;
                }

                if (read == 0)
                {
                    break;
                }

                _hasOutput = true;
                _lastOutputAtUtc = DateTime.UtcNow;
                OutputReceived?.Invoke(this, new string(buffer, 0, read));
            }
        }, cancellationToken);
    }

    private void StartExitMonitor()
    {
        _exitMonitorCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _exitMonitorCancellation.Token;

        _exitMonitorTask = Task.Run(() =>
        {
            if (_processHandle == IntPtr.Zero)
            {
                return;
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                uint wait = WaitForSingleObject(_processHandle, 100);
                if (wait == WaitTimeout)
                {
                    continue;
                }

                if (wait == 0 && !_disposed && GetExitCodeProcess(_processHandle, out uint exitCode))
                {
                    Exited?.Invoke(this, unchecked((int)exitCode));
                }

                return;
            }
        }, cancellationToken);
    }

    private void ConfigureProcessJob(IntPtr processHandle)
    {
        if (processHandle == IntPtr.Zero)
        {
            return;
        }

        if (!IsProcessInJob(processHandle, IntPtr.Zero, out bool isInJob))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to query process job membership.");
        }

        if (isInJob)
        {
            return;
        }

        IntPtr jobHandle = CreateJobObject(IntPtr.Zero, null);
        if (jobHandle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create process job.");
        }

        try
        {
            var jobInfo = new JobObjectExtendedLimitInformationState
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitKillOnJobClose
                }
            };

            if (!SetInformationJobObject(
                    jobHandle,
                    JobObjectExtendedLimitInformation,
                    ref jobInfo,
                    (uint)Marshal.SizeOf<JobObjectExtendedLimitInformationState>()))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to configure process job.");
            }

            if (!AssignProcessToJobObject(jobHandle, processHandle))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to assign process to job.");
            }

            _jobHandle = jobHandle;
            jobHandle = IntPtr.Zero;
        }
        finally
        {
            if (jobHandle != IntPtr.Zero)
            {
                CloseHandle(jobHandle);
            }
        }
    }

    private static void TryWriteExit(TextWriter? writer)
    {
        if (writer is null)
        {
            return;
        }

        try
        {
            writer.Write("exit\r\n");
            writer.Flush();
        }
        catch
        {
        }
    }

    private static async Task WaitForTaskAsync(Task? task, TimeSpan timeout)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static async Task<bool> WaitForProcessExitAsync(IntPtr processHandle, TimeSpan timeout)
    {
        if (processHandle == IntPtr.Zero)
        {
            return true;
        }

        DateTime deadlineUtc = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadlineUtc)
        {
            uint wait = WaitForSingleObject(processHandle, 50);
            if (wait == 0)
            {
                return true;
            }

            if (wait != WaitTimeout)
            {
                return false;
            }

            await Task.Delay(25).ConfigureAwait(false);
        }

        return WaitForSingleObject(processHandle, 0) == 0;
    }

    private static void DisposeQuietly(IDisposable? disposable)
    {
        if (disposable is null)
        {
            return;
        }

        try
        {
            disposable.Dispose();
        }
        catch
        {
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(
        out IntPtr hReadPipe,
        out IntPtr hWritePipe,
        ref SecurityAttributes lpPipeAttributes,
        int nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(
        IntPtr hObject,
        uint dwMask,
        uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "CreatePseudoConsole")]
    private static extern int CreatePseudoConsoleHandle(
        Coord size,
        IntPtr hInput,
        IntPtr hOutput,
        uint dwFlags,
        out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(
        IntPtr hPC,
        Coord size);

    [DllImport("kernel32.dll", SetLastError = false, EntryPoint = "ClosePseudoConsole")]
    private static extern void ClosePseudoConsoleHandle(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(
        IntPtr lpAttributeList,
        int dwAttributeCount,
        int dwFlags,
        ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList,
        uint dwFlags,
        IntPtr attribute,
        IntPtr lpValue,
        IntPtr cbSize,
        IntPtr lpPreviousValue,
        IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = false)]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateProcessW")]
    private static extern bool CreateProcess(
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref StartupInfoEx lpStartupInfo,
        out ProcessInformation lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob,
        int jobObjectInfoClass,
        ref JobObjectExtendedLimitInformationState lpJobObjectInfo,
        uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(IntPtr hJob, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(IntPtr processHandle, IntPtr jobHandle, out bool result);

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

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformationState
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
