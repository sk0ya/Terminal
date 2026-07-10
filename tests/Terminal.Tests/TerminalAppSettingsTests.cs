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

    [Fact]
    public void ScrollbackLimitDefaultsTo10000()
    {
        Assert.Equal(TerminalAppSettings.DefaultScrollbackLimit, new TerminalAppSettings().ScrollbackLimit);
        Assert.Equal(10000, TerminalAppSettings.DefaultScrollbackLimit);
    }

    [Fact]
    public void ScrollbackLimitSurvivesJsonRoundTrip()
    {
        var settings = new TerminalAppSettings { ScrollbackLimit = 25000 };

        string json = JsonSerializer.Serialize(settings);
        TerminalAppSettings? restored = JsonSerializer.Deserialize<TerminalAppSettings>(json);

        Assert.NotNull(restored);
        Assert.Equal(25000, restored!.ScrollbackLimit);
    }

    [Theory]
    [InlineData(int.MinValue, TerminalAppSettings.MinScrollbackLimit)]
    [InlineData(0, TerminalAppSettings.MinScrollbackLimit)]
    [InlineData(99, TerminalAppSettings.MinScrollbackLimit)]
    [InlineData(100, 100)]
    [InlineData(10000, 10000)]
    [InlineData(1_000_000, 1_000_000)]
    [InlineData(1_000_001, TerminalAppSettings.MaxScrollbackLimit)]
    [InlineData(int.MaxValue, TerminalAppSettings.MaxScrollbackLimit)]
    public void ClampScrollbackLimitClampsToSupportedRange(int value, int expected)
    {
        Assert.Equal(expected, TerminalAppSettings.ClampScrollbackLimit(value));
    }

    [Theory]
    [InlineData(0, TerminalAppSettings.MinVerticalTabWidth)]
    [InlineData(120, 120)]
    [InlineData(240, 240)]
    [InlineData(500, TerminalAppSettings.MaxVerticalTabWidth)]
    public void ClampVerticalTabWidthClampsToSupportedRange(double value, double expected)
    {
        Assert.Equal(expected, TerminalAppSettings.ClampVerticalTabWidth(value));
    }

    [Fact]
    public void VerticalTabPreferencesSurviveJsonRoundTrip()
    {
        var settings = new TerminalAppSettings
        {
            VerticalTabWidth = 260,
            VerticalTabsCollapsed = true
        };

        string json = JsonSerializer.Serialize(settings);
        TerminalAppSettings? restored = JsonSerializer.Deserialize<TerminalAppSettings>(json);

        Assert.NotNull(restored);
        Assert.Equal(260, restored!.VerticalTabWidth);
        Assert.True(restored.VerticalTabsCollapsed);
    }
}
