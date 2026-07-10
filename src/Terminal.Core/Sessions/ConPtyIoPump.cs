using Microsoft.Win32.SafeHandles;
using System.IO;
using System.Text;

namespace Terminal.Sessions;

internal sealed class ConPtyIoPump : IAsyncDisposable
{
    private readonly object _writeLock = new();
    private readonly Stream _input;
    private readonly StreamWriter _writer;
    private readonly StreamReader _reader;
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _readTask;
    private int _disposed;

    internal ConPtyIoPump(SafeFileHandle input, SafeFileHandle output)
        : this(new FileStream(input, FileAccess.Write, 4096, false), new FileStream(output, FileAccess.Read, 4096, false)) { }

    internal ConPtyIoPump(Stream input, Stream output)
    {
        _input = input;
        _writer = new StreamWriter(input, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        _reader = new StreamReader(output, new UTF8Encoding(false));
    }

    internal Task Start(Action<string> outputReceived)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _readTask ??= Task.Run(() => ReadLoop(outputReceived));
    }

    internal void Write(string input) { lock (_writeLock) { _writer.Write(input); _writer.Flush(); } }
    internal void Write(ReadOnlySpan<byte> input) { lock (_writeLock) { _writer.Flush(); _input.Write(input); _input.Flush(); } }

    internal void TryRequestShellExit()
    {
        try { lock (_writeLock) { _writer.Flush(); _input.WriteByte(0x03); _input.Flush(); _writer.Write("exit\r\n"); _writer.Flush(); } }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cancellation.Cancel();
        DisposeQuietly(_writer); DisposeQuietly(_input); DisposeQuietly(_reader);
        if (_readTask is not null) { try { await _readTask.WaitAsync(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false); } catch { } }
        _cancellation.Dispose();
    }

    private void ReadLoop(Action<string> outputReceived)
    {
        char[] buffer = new char[4096];
        while (!_cancellation.IsCancellationRequested)
        {
            int read;
            try { read = _reader.Read(buffer, 0, buffer.Length); } catch { return; }
            if (read == 0) return;
            outputReceived(new string(buffer, 0, read));
        }
    }

    private static void DisposeQuietly(IDisposable value) { try { value.Dispose(); } catch { } }
}
