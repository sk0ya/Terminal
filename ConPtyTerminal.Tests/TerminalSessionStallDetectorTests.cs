namespace ConPtyTerminal.Tests;

public sealed class TerminalSessionStallDetectorTests
{
    [Fact]
    public void StartupStallReturnsFalseBeforeTimeout()
    {
        DateTime startedAtUtc = DateTime.UtcNow;

        bool stalled = TerminalSessionStallDetector.IsStartupStalled(
            hasOutput: false,
            startedAtUtc,
            startedAtUtc.AddSeconds(3),
            TimeSpan.FromSeconds(4));

        Assert.False(stalled);
    }

    [Fact]
    public void StartupStallReturnsTrueAfterTimeoutWithoutOutput()
    {
        DateTime startedAtUtc = DateTime.UtcNow;

        bool stalled = TerminalSessionStallDetector.IsStartupStalled(
            hasOutput: false,
            startedAtUtc,
            startedAtUtc.AddSeconds(5),
            TimeSpan.FromSeconds(4));

        Assert.True(stalled);
    }

    [Fact]
    public void StartupStallReturnsFalseAfterAnyOutput()
    {
        DateTime startedAtUtc = DateTime.UtcNow;

        bool stalled = TerminalSessionStallDetector.IsStartupStalled(
            hasOutput: true,
            startedAtUtc,
            startedAtUtc.AddMinutes(5),
            TimeSpan.FromSeconds(4));

        Assert.False(stalled);
    }
}
