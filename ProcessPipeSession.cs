using System.Diagnostics;
using System.IO;
using System.Text;

namespace ConPtyTerminal;

public sealed class ProcessPipeSession : ITerminalSession
{
    private readonly object _syncRoot = new();
    private readonly string _commandLine;
    private Process? _process;
    private Stream? _inputStream;
    private StreamWriter? _inputWriter;
    private CancellationTokenSource? _readCancellation;
    private Task? _outputReadTask;
    private Task? _errorReadTask;
    private bool _started;
    private DateTime _startedAtUtc;
    private DateTime _lastOutputAtUtc;
    private bool _hasOutput;
    private bool _disposed;

    public event EventHandler<string>? OutputReceived;
    public event EventHandler<int>? Exited;

    public ProcessPipeSession(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            throw new ArgumentException("Command line is required.", nameof(commandLine));
        }

        _commandLine = commandLine.Trim();
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

        (string fileName, string arguments) = SplitCommandLine(_commandLine);

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        };

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
        _inputWriter.AutoFlush = true;
        _readCancellation = new CancellationTokenSource();
        _outputReadTask = StartReadLoop(_process.StandardOutput, _readCancellation.Token);
        _errorReadTask = StartReadLoop(_process.StandardError, _readCancellation.Token);
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
        _ = columns;
        _ = rows;
    }

    public bool IsOutputStalled(TimeSpan initialOutputTimeout, TimeSpan idleOutputTimeout)
    {
        if (_disposed || !_started)
        {
            return false;
        }

        if (_process is null || _process.HasExited)
        {
            return false;
        }

        DateTime now = DateTime.UtcNow;
        if (!_hasOutput)
        {
            return now - _startedAtUtc > initialOutputTimeout;
        }

        return now - _lastOutputAtUtc > idleOutputTimeout;
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
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        try
        {
            _inputWriter?.Write("exit\r\n");
            _inputWriter?.Flush();
        }
        catch
        {
        }

        _readCancellation?.Cancel();
        try
        {
            _outputReadTask?.Wait(200);
            _errorReadTask?.Wait(200);
        }
        catch
        {
        }

        if (_process is not null)
        {
            _process.Exited -= OnProcessExited;
            if (!_process.HasExited)
            {
                try
                {
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(500);
                }
                catch
                {
                }
            }
        }

        try
        {
            _inputWriter?.Dispose();
        }
        catch
        {
        }

        try
        {
            _process?.Dispose();
        }
        catch
        {
        }

        _readCancellation?.Dispose();
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

    private static (string FileName, string Arguments) SplitCommandLine(string commandLine)
    {
        string text = commandLine.Trim();
        if (text.Length == 0)
        {
            throw new ArgumentException("Command line is required.", nameof(commandLine));
        }

        if (text[0] == '"')
        {
            int endQuote = text.IndexOf('"', 1);
            if (endQuote < 0)
            {
                throw new ArgumentException("Invalid quoted command line.", nameof(commandLine));
            }

            string fileName = text.Substring(1, endQuote - 1);
            string args = text[(endQuote + 1)..].TrimStart();
            return (fileName, args);
        }

        int splitIndex = text.IndexOf(' ');
        if (splitIndex < 0)
        {
            return (text, string.Empty);
        }

        return (text[..splitIndex], text[(splitIndex + 1)..].TrimStart());
    }
}
