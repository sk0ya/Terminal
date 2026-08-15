using System.IO;

using Terminal.Logging;
using Terminal.Sessions;

namespace Terminal.Tests;

public sealed class RawSessionCaptureTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "terminal-raw-capture-tests",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(RawSessionCapture.EnvironmentVariable, null);
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void CaptureIsOffUnlessTheEnvironmentAsksForIt()
    {
        Environment.SetEnvironmentVariable(RawSessionCapture.EnvironmentVariable, null);
        var inner = new FakeSession();

        Assert.Same(inner, RawSessionCapture.WrapIfEnabled(inner, "pwsh.exe"));
    }

    [Fact]
    public void CapturedStreamKeepsEscapeSequencesVerbatimAndSeparatesInput()
    {
        Environment.SetEnvironmentVariable(RawSessionCapture.EnvironmentVariable, _directory);
        var inner = new FakeSession();

        ITerminalSession session = RawSessionCapture.WrapIfEnabled(inner, "pwsh.exe");
        Assert.NotSame(inner, session);

        string? forwarded = null;
        session.OutputReceived += (_, text) => forwarded = text;

        session.Write("ls\r");
        inner.RaiseOutput("]0;claude[2J[Hui");
        session.Dispose();

        // The wrapper stays transparent to the terminal.
        Assert.Equal("ls\r", Assert.Single(inner.Writes));
        Assert.Equal("]0;claude[2J[Hui", forwarded);

        string captured = File.ReadAllText(Directory.EnumerateFiles(_directory).Single());
        Assert.Contains("]0;claude[2J[Hui", captured, StringComparison.Ordinal);
        Assert.Contains("<IN>ls\r</IN>", captured, StringComparison.Ordinal);
        Assert.Contains("<IN>launch pwsh.exe</IN>", captured, StringComparison.Ordinal);
    }

    [Fact]
    public void ResizesAreRecordedBecauseTheyReflowBothScreenModels()
    {
        Environment.SetEnvironmentVariable(RawSessionCapture.EnvironmentVariable, _directory);
        var inner = new FakeSession();

        ITerminalSession session = RawSessionCapture.WrapIfEnabled(inner, "pwsh.exe");
        session.Resize(100, 30);
        session.Dispose();

        Assert.Equal((100, 30), Assert.Single(inner.Resizes));
        string captured = File.ReadAllText(Directory.EnumerateFiles(_directory).Single());
        Assert.Contains("<IN>resize 100x30</IN>", captured, StringComparison.Ordinal);
    }

    private sealed class FakeSession : ITerminalSession
    {
        public List<string> Writes { get; } = [];
        public List<(short Columns, short Rows)> Resizes { get; } = [];

        public TerminalSessionCapabilities Capabilities { get; } = new(
            TerminalSessionKind.ConPty,
            SupportsResize: true,
            SupportsTerminalInput: true);

        public event EventHandler<string>? OutputReceived;
        public event EventHandler<int>? Exited;

        public void RaiseOutput(string text) => OutputReceived?.Invoke(this, text);

        public void Start()
        {
        }

        public void Write(string input) => Writes.Add(input);

        public void Write(byte[] input) => Writes.Add(System.Text.Encoding.UTF8.GetString(input));

        public void Resize(short columns, short rows) => Resizes.Add((columns, rows));

        public bool IsOutputStalled(TimeSpan initialOutputTimeout, TimeSpan idleOutputTimeout) => false;

        public bool TryForceUnlock(uint exitCode = 1) => false;

        public void Dispose() => _ = Exited;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
