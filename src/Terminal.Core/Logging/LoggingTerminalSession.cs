using System.Text;

using Terminal.Sessions;

namespace Terminal.Logging;

internal sealed class LoggingTerminalSession : ITerminalSession
{
    private readonly ITerminalSession _inner;
    private readonly ISessionLogger _logger;
    private readonly string _command;
    private readonly string _cwd;
    private readonly short _cols;
    private readonly short _rows;
    private readonly string _tool;

    private readonly object _lock = new();
    private readonly StringBuilder _inputBuffer = new();
    private string? _pendingEcho;
    private string? _lastOutput;
    private string? _pendingOutput;
    private System.Threading.Timer? _debounceTimer;
    private const int DebounceMs = 200;
    private bool _inEscapeSeq;

    public TerminalSessionCapabilities Capabilities => _inner.Capabilities;

    public event EventHandler<string>? OutputReceived;
    public event EventHandler<int>? Exited;

    public LoggingTerminalSession(
        ITerminalSession inner,
        ISessionLogger logger,
        string command,
        string cwd,
        short cols,
        short rows)
    {
        _inner = inner;
        _logger = logger;
        _command = command;
        _cwd = cwd;
        _cols = cols;
        _rows = rows;
        _tool = SessionLogWriter.DetectTool(command);

        _inner.OutputReceived += OnInnerOutputReceived;
        _inner.Exited += OnInnerExited;
    }

    public void Start()
    {
        _inner.Start();
        int pid = (_inner is ConPtySession conpty) ? conpty.ProcessId : 0;
        _logger.LogSessionStart(_tool, _command, _cwd, pid, _cols, _rows);
    }

    public void Write(string input)
    {
        _inner.Write(input);
        BufferInput(input);
    }

    public void Write(byte[] input)
    {
        _inner.Write(input);
        if (input.Length == 0)
        {
            return;
        }

        try
        {
            BufferInput(Encoding.UTF8.GetString(input));
        }
        catch
        {
        }
    }

    public void Resize(short columns, short rows) => _inner.Resize(columns, rows);

    public bool IsOutputStalled(TimeSpan initialOutputTimeout, TimeSpan idleOutputTimeout) =>
        _inner.IsOutputStalled(initialOutputTimeout, idleOutputTimeout);

    public bool TryForceUnlock(uint exitCode = 1) => _inner.TryForceUnlock(exitCode);

    public void Dispose()
    {
        Detach();
        FlushPendingOutput();
        _inner.Dispose();
        _logger.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        Detach();
        FlushPendingOutput();
        await _inner.DisposeAsync().ConfigureAwait(false);
        _logger.Dispose();
    }

    private void Detach()
    {
        _inner.OutputReceived -= OnInnerOutputReceived;
        _inner.Exited -= OnInnerExited;
    }

    private void FlushPendingOutput()
    {
        string? output;
        lock (_lock)
        {
            output = _pendingOutput;
            _pendingOutput = null;
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }

        if (output is not null && output != _lastOutput)
        {
            _lastOutput = output;
            _logger.LogOutput(output);
        }
    }

    private void BufferInput(string text)
    {
        lock (_lock)
        {
            foreach (char c in text)
            {
                if (c == '\x1b') { _inEscapeSeq = true; continue; }
                if (_inEscapeSeq)
                {
                    // Final byte of a CSI/SS3 sequence ends at '@'–'~' (0x40–0x7E).
                    if (c >= '@' && c <= '~') _inEscapeSeq = false;
                    continue;
                }

                if (c == '\r' || c == '\n')
                {
                    FlushInputBuffer();
                }
                else if (c == '\b' || c == '\x7f')
                {
                    if (_inputBuffer.Length > 0)
                    {
                        _inputBuffer.Remove(_inputBuffer.Length - 1, 1);
                    }
                }
                else if (c >= ' ' || c == '\t')
                {
                    _inputBuffer.Append(c);
                }
            }
        }
    }

    private void FlushInputBuffer()
    {
        if (_inputBuffer.Length == 0)
        {
            return;
        }

        string text = _inputBuffer.ToString();
        _inputBuffer.Clear();
        _pendingEcho = text;
        _logger.LogInput(text + "\n");
    }

    private void OnInnerOutputReceived(object? sender, string text)
    {
        string stripped = SessionLogWriter.StripAnsi(text);

        string? echo;
        lock (_lock)
        {
            echo = _pendingEcho;
            _pendingEcho = null;
        }

        if (echo is not null && stripped.StartsWith(echo, StringComparison.Ordinal))
        {
            stripped = stripped[echo.Length..].TrimStart('\n');
        }

        int contentChars = 0;
        foreach (char c in stripped)
        {
            if (!char.IsWhiteSpace(c)) contentChars++;
        }

        if (contentChars > 1 && stripped != _lastOutput)
        {
            lock (_lock)
            {
                _pendingOutput = stripped;
                _debounceTimer?.Dispose();
                _debounceTimer = new System.Threading.Timer(_ => FlushPendingOutput(), null, DebounceMs, System.Threading.Timeout.Infinite);
            }
        }

        OutputReceived?.Invoke(this, text);
    }

    private void OnInnerExited(object? sender, int exitCode)
    {
        FlushPendingOutput();
        _logger.LogSessionEnd(exitCode);
        Exited?.Invoke(this, exitCode);
    }
}
