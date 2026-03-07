namespace ConPtyTerminal;

public interface ITerminalSession : IDisposable, IAsyncDisposable
{
    TerminalSessionCapabilities Capabilities { get; }
    event EventHandler<string>? OutputReceived;
    event EventHandler<int>? Exited;

    void Start();
    void Write(string input);
    void Write(byte[] input);
    void Resize(short columns, short rows);
    bool IsOutputStalled(TimeSpan initialOutputTimeout, TimeSpan idleOutputTimeout);
    bool TryForceUnlock(uint exitCode = 1);
}
