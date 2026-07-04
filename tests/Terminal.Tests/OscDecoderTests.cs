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

    [Theory]
    [InlineData(";?", OscClipboardKind.Query, "c", null)]
    [InlineData("s0;?", OscClipboardKind.Query, "s0", null)]
    [InlineData("c;", OscClipboardKind.Set, "c", "")]
    [InlineData("c;dGVzdA", OscClipboardKind.Set, "c", "test")]
    public void DecodeClipboardHandlesQuerySetAndUnpaddedBase64(
        string raw,
        object kind,
        string targets,
        string? text)
    {
        OscClipboardPayload payload = OscDecoder.DecodeClipboard(raw);

        Assert.Equal((OscClipboardKind)kind, payload.Kind);
        Assert.Equal(targets, payload.SelectionTargets);
        Assert.Equal(text, payload.Text);
    }

    [Theory]
    [InlineData("missing-separator")]
    [InlineData("c;%%%")]
    public void DecodeClipboardReturnsInvalidForMalformedPayload(string raw)
    {
        Assert.Equal(OscClipboardKind.Invalid, OscDecoder.DecodeClipboard(raw).Kind);
    }

    [Fact]
    public void DecodePaletteChangesReturnsValidInRangePairsOnly()
    {
        OscPaletteChange[] changes = OscDecoder.DecodePaletteChanges(
            "1;?;2;#123456;99;?;3;bad;4",
            paletteLength: 16);

        Assert.Equal(2, changes.Length);
        Assert.Equal(new OscPaletteChange(1, OscPaletteChangeKind.Query), changes[0]);
        Assert.Equal(
            new OscPaletteChange(2, OscPaletteChangeKind.Set, Color.FromRgb(0x12, 0x34, 0x56)),
            changes[1]);
    }

    [Fact]
    public void DecodePaletteResetDistinguishesAllFromValidIndices()
    {
        OscPaletteReset all = OscDecoder.DecodePaletteReset(string.Empty, paletteLength: 16);
        OscPaletteReset selected = OscDecoder.DecodePaletteReset("1;bad;16;-1;1;3", paletteLength: 16);

        Assert.True(all.ResetAll);
        Assert.Empty(all.Indices);
        Assert.False(selected.ResetAll);
        Assert.Equal([1, 1, 3], selected.Indices);
    }

    [Theory]
    [InlineData("file://localhost/C:/Users/A%20B", "C:/Users/A B")]
    [InlineData("file:///home/user/project", "/home/user/project")]
    [InlineData("/bare/path", "/bare/path")]
    public void TryDecodeCurrentDirectoryConvertsFileUriAndPreservesBarePath(string raw, string expected)
    {
        Assert.True(OscDecoder.TryDecodeCurrentDirectory(raw, out string path));
        Assert.Equal(expected, path);
    }

    [Fact]
    public void TryDecodeCurrentDirectoryRejectsEmptyValue()
    {
        Assert.False(OscDecoder.TryDecodeCurrentDirectory(string.Empty, out _));
    }
}
