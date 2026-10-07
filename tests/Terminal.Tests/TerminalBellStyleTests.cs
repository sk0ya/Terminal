using Terminal.Settings;
using Terminal.Tabs;

namespace Terminal.Tests;

public sealed class TerminalBellStyleTests
{
    [Theory]
    [InlineData(null, TerminalBellStyle.Audible)]
    [InlineData("", TerminalBellStyle.Audible)]
    [InlineData("audible", TerminalBellStyle.Audible)]
    [InlineData(" Visual ", TerminalBellStyle.Visual)]
    [InlineData("both", TerminalBellStyle.Both)]
    [InlineData("none", TerminalBellStyle.None)]
    [InlineData("off", TerminalBellStyle.None)]
    [InlineData("bogus", TerminalBellStyle.Audible)]
    public void ParseBellStyleMapsSettingsValues(string? value, TerminalBellStyle expected)
    {
        Assert.Equal(expected, TerminalTabView.ParseBellStyle(value));
    }

    [Fact]
    public void BellStyleDefaultsToAudible()
    {
        Assert.Equal("audible", new TerminalAppSettings().BellStyle);
    }

    [Fact]
    public void ApplySettingsSetsTheTabsBellStyle()
    {
        StaTestRunner.Run(() =>
        {
            var view = new TerminalTabView("cmd.exe", Environment.CurrentDirectory);
            view.ApplySettings(new TerminalAppSettings { BellStyle = "visual" }, applyLaunchSettings: false);
            Assert.Equal(TerminalBellStyle.Visual, view.BellStyle);
        });
    }
}
