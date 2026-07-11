using Terminal.Settings;

namespace Terminal.Tests;

public sealed class TerminalKeyBindingCatalogTests
{
    [Theory]
    [InlineData("control + shift + t", "Ctrl+Shift+t")]
    [InlineData("Alt+F4", "Alt+F4")]
    [InlineData("Ctrl+OemPlus", "Ctrl+OemPlus")]
    public void ChordsAreNormalized(string input, string expected)
    {
        Assert.True(TerminalKeyBindingCatalog.TryNormalizeChord(input, out string chord));
        Assert.Equal(expected, chord);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+Ctrl+C")]
    [InlineData("Meta+C")]
    public void InvalidChordsAreRejected(string input)
    {
        Assert.False(TerminalKeyBindingCatalog.TryNormalizeChord(input, out _));
    }

    [Fact]
    public void DuplicateChordsAreReported()
    {
        var bindings = new Dictionary<string, string> { ["Copy"] = "Ctrl+C", ["Paste"] = "control+c" };
        var conflicts = TerminalKeyBindingCatalog.FindConflicts(bindings);
        Assert.Equal(["Copy", "Paste"], conflicts["Ctrl+C"]);
    }

    [Fact]
    public void NormalizeFillsMissingDefaultsAndIgnoresUnknownActions()
    {
        var normalized = TerminalKeyBindingCatalog.Normalize(new Dictionary<string, string>
        {
            ["NewTab"] = "Alt+T",
            ["Unknown"] = "Ctrl+U"
        });
        Assert.Equal("Alt+T", normalized["NewTab"]);
        Assert.Equal(TerminalKeyBindingCatalog.Defaults["Copy"], normalized["Copy"]);
        Assert.DoesNotContain("Unknown", normalized.Keys);
    }
}
