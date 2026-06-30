using System.Text.Json;

using Terminal.Settings;

namespace Terminal.Tests;

public sealed class TerminalAppSettingsTests
{
    [Fact]
    public void EnableFontLigaturesDefaultsToFalse()
    {
        Assert.False(new TerminalAppSettings().EnableFontLigatures);
    }

    [Fact]
    public void EnableFontLigaturesSurvivesJsonRoundTrip()
    {
        var settings = new TerminalAppSettings { EnableFontLigatures = true };

        string json = JsonSerializer.Serialize(settings);
        TerminalAppSettings? restored = JsonSerializer.Deserialize<TerminalAppSettings>(json);

        Assert.NotNull(restored);
        Assert.True(restored!.EnableFontLigatures);
    }
}
