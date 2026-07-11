using System.Windows.Input;
using Terminal.Input;

namespace Terminal.Tests;

public sealed class TerminalKeyBindingsTests
{
    [Fact]
    public void MatchesConfiguredChordExactly()
    {
        var bindings = new TerminalKeyBindings(new Dictionary<string, string> { ["NewTab"] = "Alt+N" });
        Assert.True(bindings.Matches("NewTab", Key.N, ModifierKeys.Alt));
        Assert.False(bindings.Matches("NewTab", Key.N, ModifierKeys.Control | ModifierKeys.Alt));
    }

    [Fact]
    public void InvalidOverrideFallsBackToDefault()
    {
        var bindings = new TerminalKeyBindings(new Dictionary<string, string> { ["NewTab"] = "Meta+N" });
        Assert.True(bindings.Matches("NewTab", Key.T, ModifierKeys.Control | ModifierKeys.Shift));
    }
}
