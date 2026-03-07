using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ConPtyTerminal;

public sealed class ProcessPipeSession : ITerminalSession
{
    private readonly object _syncRoot = new();
    private readonly string _commandLine;
    private readonly string? _workingDirectory;
    private short _columns;
    private short _rows;
    private Process? _process;
    private Stream? _inputStream;
    private StreamWriter? _inputWriter;
    private StreamReader? _outputReader;
    private StreamReader? _errorReader;
    private CancellationTokenSource? _readCancellation;
    private Task? _outputReadTask;
    private Task? _errorReadTask;
    private bool _started;
    private DateTime _startedAtUtc;
    private DateTime _lastOutputAtUtc;
    private bool _hasOutput;
    private bool _disposed;

    public TerminalSessionCapabilities Capabilities { get; } = new(
        TerminalSessionKind.Compatibility,
        SupportsResize: false,
        SupportsTerminalInput: false);

    public event EventHandler<string>? OutputReceived;
    public event EventHandler<int>? Exited;

    public ProcessPipeSession(string commandLine)
        : this(commandLine, null, null, null)
    {
    }

    public ProcessPipeSession(string commandLine, short? columns, short? rows, string? workingDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            throw new ArgumentException("Command line is required.", nameof(commandLine));
        }

        if (!string.IsNullOrWhiteSpace(workingDirectory) && !Directory.Exists(workingDirectory))
        {
            throw new DirectoryNotFoundException($"Working directory was not found: {workingDirectory}");
        }

        _commandLine = commandLine.Trim();
        _columns = columns.GetValueOrDefault();
        _rows = rows.GetValueOrDefault();
        _workingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory;
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

        (string fileName, string[] arguments) = SplitCommandLine(_commandLine);

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        };

        if (!string.IsNullOrWhiteSpace(_workingDirectory))
        {
            startInfo.WorkingDirectory = _workingDirectory;
        }

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["TERM"] = "dumb";
        if (_columns > 0)
        {
            startInfo.Environment["COLUMNS"] = _columns.ToString();
        }

        if (_rows > 0)
        {
            startInfo.Environment["LINES"] = _rows.ToString();
        }

        _process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
        _process.Exited += OnProcessExited;

        if (!_process.Start())
        {
            throw new InvalidOperationException($"Failed to launch command: {_commandLine}");
        }

        _inputWriter = _process.StandardInput;
        _inputStream = _inputWriter.BaseStream;
        _outputReader = _process.StandardOutput;
        _errorReader = _process.StandardError;
        _inputWriter.AutoFlush = true;
        _readCancellation = new CancellationTokenSource();
        _outputReadTask = StartReadLoop(_outputReader, _readCancellation.Token);
        _errorReadTask = StartReadLoop(_errorReader, _readCancellation.Token);
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
        _columns = columns;
        _rows = rows;
    }

    public bool IsOutputStalled(TimeSpan initialOutputTimeout, TimeSpan idleOutputTimeout)
    {
        _ = idleOutputTimeout;

        if (_disposed || !_started)
        {
            return false;
        }

        if (_process is null || _process.HasExited)
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
        _ = exitCode;

        if (_disposed || _process is null || _process.HasExited)
        {
            return false;
        }

        try
        {
            _process.Kill(entireProcessTree: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        Process? process;
        StreamWriter? inputWriter;
        Stream? inputStream;
        StreamReader? outputReader;
        StreamReader? errorReader;
        CancellationTokenSource? readCancellation;
        Task? outputReadTask;
        Task? errorReadTask;

        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            process = _process;
            inputWriter = _inputWriter;
            inputStream = _inputStream;
            outputReader = _outputReader;
            errorReader = _errorReader;
            readCancellation = _readCancellation;
            outputReadTask = _outputReadTask;
            errorReadTask = _errorReadTask;

            _process = null;
            _inputWriter = null;
            _inputStream = null;
            _outputReader = null;
            _errorReader = null;
            _readCancellation = null;
            _outputReadTask = null;
            _errorReadTask = null;
        }

        TryWriteExit(inputWriter);

        readCancellation?.Cancel();
        DisposeQuietly(outputReader);
        DisposeQuietly(errorReader);

        await WaitForTaskAsync(outputReadTask, TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
        await WaitForTaskAsync(errorReadTask, TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);

        if (process is not null)
        {
            process.Exited -= OnProcessExited;
            await EnsureProcessStoppedAsync(process).ConfigureAwait(false);
        }

        await WaitForTaskAsync(outputReadTask, TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
        await WaitForTaskAsync(errorReadTask, TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);

        DisposeQuietly(inputWriter);
        DisposeQuietly(inputStream);
        DisposeQuietly(outputReader);
        DisposeQuietly(errorReader);
        DisposeQuietly(process);
        DisposeQuietly(readCancellation);

        GC.SuppressFinalize(this);
    }

    private Task StartReadLoop(StreamReader reader, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            char[] buffer = new char[4096];
            while (!cancellationToken.IsCancellationRequested)
            {
                int read;
                try
                {
                    read = reader.Read(buffer, 0, buffer.Length);
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

    private void OnProcessExited(object? sender, EventArgs e)
    {
        if (_process is null)
        {
            return;
        }

        Exited?.Invoke(this, _process.ExitCode);
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

    private static async Task EnsureProcessStoppedAsync(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }
        }
        catch
        {
            return;
        }

        if (await TryWaitForExitAsync(process, TimeSpan.FromMilliseconds(250)).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
        }

        _ = await TryWaitForExitAsync(process, TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
    }

    private static async Task<bool> TryWaitForExitAsync(Process process, TimeSpan timeout)
    {
        try
        {
            await process.WaitForExitAsync().WaitAsync(timeout).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
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

    internal static (string FileName, string[] Arguments) SplitCommandLine(string commandLine)
    {
        string text = commandLine.Trim();
        if (text.Length == 0)
        {
            throw new ArgumentException("Command line is required.", nameof(commandLine));
        }

        int argumentCount;
        IntPtr argv = CommandLineToArgvW(text, out argumentCount);
        if (argv == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to parse command line.");
        }

        try
        {
            if (argumentCount <= 0)
            {
                throw new ArgumentException("Command line is required.", nameof(commandLine));
            }

            var parsedArguments = new string[argumentCount];
            for (int index = 0; index < argumentCount; index++)
            {
                IntPtr valuePtr = Marshal.ReadIntPtr(argv, index * IntPtr.Size);
                parsedArguments[index] = Marshal.PtrToStringUni(valuePtr) ?? string.Empty;
            }

            return (parsedArguments[0], parsedArguments.Skip(1).ToArray());
        }
        finally
        {
            _ = LocalFree(argv);
        }
    }

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int argumentCount);

    [DllImport("kernel32.dll", SetLastError = false)]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
