namespace Terminal.Sessions;

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

    /// <summary>
    /// Makes <paramref name="ownerWindow"/> the owner of the session's hidden console window, so dialogs that
    /// programs attach to their console (credential pickers, sign-in prompts) open in front of the window
    /// showing the terminal instead of behind it. Call again when the terminal moves to another window.
    /// Sessions that have no console window of their own ignore it (the default).
    /// </summary>
    void SetOwnerWindow(IntPtr ownerWindow)
    {
    }
}
