using Terminal;

namespace Terminal.Tests;

public sealed class TerminalCloseConfirmationTests
{
    [Fact]
    public void EmptyOrIdleTabsDoNotNeedConfirmation()
    {
        Assert.False(TerminalCloseConfirmation.NeedsConfirmation([]));
        Assert.False(TerminalCloseConfirmation.NeedsConfirmation([false, false]));
    }

    [Fact]
    public void AnyBusyTabNeedsConfirmation()
    {
        Assert.True(TerminalCloseConfirmation.NeedsConfirmation([false, true, false]));
    }
}
