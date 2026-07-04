using System.Windows.Media;

using Terminal.Buffer;

namespace Terminal.Tests;

public sealed class OscDecoderTests
{
    [Theory]
    [InlineData("104", "104", "")]
    [InlineData("9;Build done", "9", "Build done")]
    [InlineData("633;E;echo a;b", "633", "E;echo a;b")]
    public void DecodeSplitsOnlyTheCommandSeparator(string raw, string command, string value)
    {
        Assert.Equal(new OscCommand(command, value), OscDecoder.Decode(raw));
    }

    [Theory]
    [InlineData("4;1;75", 1, 75)]
    [InlineData("4;2;999", 2, 100)]
    [InlineData("4;3;-5", 3, 0)]
    [InlineData("4;4;bad", 4, 0)]
    public void TryDecodeTaskbarProgressValidatesStateAndClampsProgress(
        string raw,
        int state,
        int percentage)
    {
        Assert.True(OscDecoder.TryDecodeTaskbarProgress(raw, out OscTaskbarProgress progress));
        Assert.Equal(new OscTaskbarProgress(state, percentage), progress);
    }

    [Theory]
    [InlineData("4")]
    [InlineData("4;5;20")]
    [InlineData("4;x;20")]
    public void TryDecodeTaskbarProgressRejectsInvalidState(string raw)
    {
        Assert.False(OscDecoder.TryDecodeTaskbarProgress(raw, out _));
    }

    [Fact]
    public void DecodeShellCommandUnescapesSeparatorsControlsAndBackslash()
    {
        OscShellPayload payload = OscDecoder.DecodeShellIntegration("E;echo a\\x3bb\\x0a\\\\");

        Assert.Equal(OscShellPayloadKind.CommandLine, payload.Kind);
        Assert.Equal("echo a;b\n\\", payload.Value);
    }

    [Fact]
    public void DecodeShellPropertySeparatesNameAndDecodedValue()
    {
        OscShellPayload payload = OscDecoder.DecodeShellIntegration("P;HistoryPath=C:\\Temp\\x20History.txt");

        Assert.Equal(OscShellPayloadKind.Property, payload.Kind);
        Assert.Equal("HistoryPath", payload.PropertyName);
        Assert.Equal("C:\\Temp History.txt", payload.Value);
    }

    [Theory]
    [InlineData("A", ShellCommandZoneType.PromptStart, null)]
    [InlineData("B", ShellCommandZoneType.CommandStart, null)]
    [InlineData("C", ShellCommandZoneType.CommandExecuted, null)]
    [InlineData("D;42;nonce", ShellCommandZoneType.CommandDone, 42)]
    [InlineData("D;bad", ShellCommandZoneType.CommandDone, null)]
    public void DecodeShellZoneReturnsTypeAndOptionalExitCode(
        string raw,
        object zone,
        int? exitCode)
    {
        OscShellPayload payload = OscDecoder.DecodeShellIntegration(raw);

        Assert.Equal(OscShellPayloadKind.Zone, payload.Kind);
        Assert.Equal((ShellCommandZoneType)zone, payload.ZoneType);
        Assert.Equal(exitCode, payload.ExitCode);
    }

    [Theory]
    [InlineData("rgb:ff/00/80", 255, 0, 128)]
    [InlineData("RGB:0a/0b/0c", 10, 11, 12)]
    [InlineData("#123456", 18, 52, 86)]
    public void TryParseColorAcceptsSupportedFormats(string raw, byte red, byte green, byte blue)
    {
        Assert.True(OscDecoder.TryParseColor(raw, out Color color));
        Assert.Equal(Color.FromRgb(red, green, blue), color);
    }

    [Theory]
    [InlineData("rgb:ff/00")]
    [InlineData("rgb:xx/00/00")]
    [InlineData("#12345")]
    [InlineData("red")]
    public void TryParseColorRejectsInvalidFormats(string raw)
    {
        Assert.False(OscDecoder.TryParseColor(raw, out _));
    }
}
