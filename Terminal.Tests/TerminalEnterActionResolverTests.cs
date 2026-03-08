using System.Windows.Input;

namespace Terminal.Tests;

public sealed class TerminalEnterActionResolverTests
{
    [Fact]
    public void ResolveForProxyReturnsFlushWhenProxyTextIsPending()
    {
        Assert.Equal(
            TerminalEnterAction.FlushPendingProxyText,
            TerminalEnterActionResolver.ResolveForProxy(Key.Enter, hasPendingProxyText: true));
    }

    [Fact]
    public void ResolveForProxyReturnsSendWhenNoProxyTextIsPending()
    {
        Assert.Equal(
            TerminalEnterAction.SendToTerminal,
            TerminalEnterActionResolver.ResolveForProxy(Key.Enter, hasPendingProxyText: false));
    }

    [Fact]
    public void ResolveForProxyIgnoresNonEnterKeys()
    {
        Assert.Equal(
            TerminalEnterAction.None,
            TerminalEnterActionResolver.ResolveForProxy(Key.Tab, hasPendingProxyText: true));
    }
}
