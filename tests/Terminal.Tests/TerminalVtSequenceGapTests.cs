using System.Collections.Generic;
using System.Windows.Media;

using Terminal.Buffer;
using Terminal.Settings;

namespace Terminal.Tests;

// Covers VT/ANSI sequences added to close terminal-compatibility gaps:
// OSC 10/11/12 set + OSC 104/110/111/112 reset, DECRQSS cursor-style query,
// DEC private modes 8/45/1034/1036/1039, and Media Copy (CSI i).
public sealed class TerminalVtSequenceGapTests
{
    [Fact]
    public void Osc104ResetsSpecificPaletteEntryToThemeDefault()
    {
        var buffer = new AnsiTerminalBuffer(32, 3);
        Color defaultColor1 = TerminalColorTheme.Default.AnsiPalette.ToArray()[1];

        buffer.Process("]4;1;rgb:00/ff/00");
        buffer.Process("[31mA");
        buffer.Process("]104;1");
        buffer.Process("[31mB");

        AnsiTerminalBuffer.TerminalRenderSnapshot snapshot = buffer.CreateRenderSnapshot(showCursor: false);
        Assert.Equal(Color.FromRgb(0, 0xff, 0), snapshot.Lines[0].Segments[0].Foreground);
        Assert.Equal(defaultColor1, snapshot.Lines[0].Segments[1].Foreground);
    }

    [Fact]
    public void Osc104WithoutParameterResetsEntirePalette()
    {
        var buffer = new AnsiTerminalBuffer(32, 3);
        Color defaultColor2 = TerminalColorTheme.Default.AnsiPalette.ToArray()[2];

        buffer.Process("]4;2;rgb:12/34/56");
        buffer.Process("]104");
        buffer.Process("[32mA");

        AnsiTerminalBuffer.TerminalRenderSnapshot snapshot = buffer.CreateRenderSnapshot(showCursor: false);
        Assert.Equal(defaultColor2, snapshot.Lines[0].Segments[0].Foreground);
    }

    [Fact]
    public void Osc11SetsBackgroundAndOsc111ResetsIt()
    {
        var buffer = new AnsiTerminalBuffer(32, 3);
        buffer.Process("X");

        buffer.Process("]11;rgb:01/02/03");
        AnsiTerminalBuffer.TerminalRenderSnapshot afterSet = buffer.CreateRenderSnapshot(showCursor: false);
        Assert.Equal(Color.FromRgb(1, 2, 3), afterSet.Lines[0].Segments[0].Background);

        buffer.Process("]111");
        AnsiTerminalBuffer.TerminalRenderSnapshot afterReset = buffer.CreateRenderSnapshot(showCursor: false);
        Assert.Equal(TerminalColorTheme.Default.Background, afterReset.Lines[0].Segments[0].Background);
    }

    [Fact]
    public void Osc10SetsForegroundAndOsc110ResetsIt()
    {
        var buffer = new AnsiTerminalBuffer(32, 3);
        buffer.Process("X");

        buffer.Process("]10;rgb:0a/0b/0c");
        AnsiTerminalBuffer.TerminalRenderSnapshot afterSet = buffer.CreateRenderSnapshot(showCursor: false);
        Assert.Equal(Color.FromRgb(0x0a, 0x0b, 0x0c), afterSet.Lines[0].Segments[0].Foreground);

        buffer.Process("]110");
        AnsiTerminalBuffer.TerminalRenderSnapshot afterReset = buffer.CreateRenderSnapshot(showCursor: false);
        Assert.Equal(TerminalColorTheme.Default.Foreground, afterReset.Lines[0].Segments[0].Foreground);
    }

    [Fact]
    public void Osc12SetCursorColorIsReflectedInQuery()
    {
        var buffer = new AnsiTerminalBuffer(32, 3);
        buffer.Process("]12;rgb:0a/0b/0c");

        string? emitted = null;
        buffer.InputSequenceGenerated += (_, text) => emitted = text;
        buffer.Process("]12;?");

        Assert.NotNull(emitted);
        Assert.Contains("]12;rgb:0a0a/0b0b/0c0c", emitted);
    }

    [Fact]
    public void DecrqssReportsCurrentCursorStyle()
    {
        var buffer = new AnsiTerminalBuffer(32, 3);
        buffer.Process("[3 q"); // DECSCUSR: underline, blinking → Ps 3

        string? emitted = null;
        buffer.InputSequenceGenerated += (_, text) => emitted = text;
        buffer.Process("P$q q\\"); // DECRQSS query of DECSCUSR

        Assert.Equal("P1$r3 q\\", emitted);
    }

    [Fact]
    public void DecrqssReportsCompleteCurrentSgr()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);
        buffer.Process("[1;2;3;4:3;5;7;8;9;53;38;2;1;2;3;48;2;4;5;6;58;2;7;8;9m");

        string? emitted = null;
        buffer.InputSequenceGenerated += (_, text) => emitted = text;
        buffer.Process("P$qm\\");

        Assert.Equal("P1$r0;1;2;3;4:3;5;7;8;9;53;38;2;1;2;3;48;2;4;5;6;58;2;7;8;9m\\", emitted);
    }

    [Fact]
    public void DecrqssReportsCurrentRegionsAndDimensions()
    {
        var buffer = new AnsiTerminalBuffer(40, 12);
        buffer.Process("[3;9r[?69h[4;30s");
        var emitted = new List<string>();
        buffer.InputSequenceGenerated += (_, text) => emitted.Add(text);

        buffer.Process("P$qr\\P$qs\\P$qt\\P$q$|\\P$q*|\\");

        Assert.Equal(
            [
                "P1$r3;9r\\",
                "P1$r4;30s\\",
                "P1$r12t\\",
                "P1$r40$|\\",
                "P1$r12*|\\"
            ],
            emitted);
    }

    [Fact]
    public void DecrqssReportsFixedConformanceAndErasableCharacterAttributes()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);
        var emitted = new List<string>();
        buffer.InputSequenceGenerated += (_, text) => emitted.Add(text);

        buffer.Process("P$q\"p\\P$q\"q\\");

        Assert.Equal(["P1$r62;1\"p\\", "P1$r0\"q\\"], emitted);
    }

    [Fact]
    public void DecrqssReportsModifyOtherKeysAndRejectsUnsupportedRequests()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);
        buffer.Process("[>4;2m");
        var emitted = new List<string>();
        buffer.InputSequenceGenerated += (_, text) => emitted.Add(text);

        buffer.Process("P$q>4m\\P$q*x\\");

        Assert.Equal(["P1$r>4;2m\\", "P0$r\\"], emitted);
    }

    [Fact]
    public void ReverseWraparoundBackspaceWrapsToPreviousLine()
    {
        var buffer = new AnsiTerminalBuffer(10, 3);
        buffer.Process("[?45h");
        buffer.Process("\r\n"); // row 1, column 0
        buffer.Process("\b");

        Assert.Equal(0, buffer.CursorRow);
        Assert.Equal(19, buffer.CursorColumn); // MinColumns clamps width to 20 → right edge is column 19
    }

    [Fact]
    public void BackspaceAtLeftEdgeStaysWhenReverseWraparoundDisabled()
    {
        var buffer = new AnsiTerminalBuffer(10, 3);
        buffer.Process("\r\n"); // row 1, column 0
        buffer.Process("\b");

        Assert.Equal(1, buffer.CursorRow);
        Assert.Equal(0, buffer.CursorColumn);
    }

    [Fact]
    public void AltSendsEscapeDefaultsOnAndTogglesWithMode1039()
    {
        var buffer = new AnsiTerminalBuffer(10, 3);
        Assert.True(buffer.AltSendsEscape);

        buffer.Process("[?1039l");
        Assert.False(buffer.AltSendsEscape);

        buffer.Process("[?1039h");
        Assert.True(buffer.AltSendsEscape);
    }

    [Fact]
    public void Mode1036AlsoControlsAltSendsEscape()
    {
        var buffer = new AnsiTerminalBuffer(10, 3);

        buffer.Process("[?1036l");
        Assert.False(buffer.AltSendsEscape);

        buffer.Process("[?1036h");
        Assert.True(buffer.AltSendsEscape);
    }

    [Fact]
    public void Mode1039DecrqmReportsCurrentState()
    {
        var buffer = new AnsiTerminalBuffer(10, 3);
        var responses = new List<string>();
        buffer.InputSequenceGenerated += (_, text) => responses.Add(text);

        buffer.Process("[?1039$p");
        buffer.Process("[?1039l");
        buffer.Process("[?1039$p");

        Assert.Contains("[?1039;1$y", responses);
        Assert.Contains("[?1039;2$y", responses);
    }

    [Fact]
    public void Mode45DecrqmReportsCurrentState()
    {
        var buffer = new AnsiTerminalBuffer(10, 3);
        string? emitted = null;
        buffer.InputSequenceGenerated += (_, text) => emitted = text;

        buffer.Process("[?45h");
        buffer.Process("[?45$p");

        Assert.Equal("[?45;1$y", emitted);
    }

    [Fact]
    public void Mode8DecrqmDefaultsEnabled()
    {
        var buffer = new AnsiTerminalBuffer(10, 3);
        string? emitted = null;
        buffer.InputSequenceGenerated += (_, text) => emitted = text;

        buffer.Process("[?8$p");

        Assert.Equal("[?8;1$y", emitted);
    }

    [Fact]
    public void MediaCopyCsiIisConsumedWithoutPrinting()
    {
        var buffer = new AnsiTerminalBuffer(10, 3);
        buffer.Process("[5iX");

        Assert.Equal("X", buffer.GetScreenLineText(0).TrimEnd());
        Assert.Equal(1, buffer.CursorColumn);
    }

    [Fact]
    public void UnknownCsiFinalIsCountedWithoutPrinting()
    {
        var buffer = new AnsiTerminalBuffer(32, 3);

        buffer.Process("\u001b[1~X");

        Assert.Equal(1, buffer.UnknownCsiSequenceCount);
        Assert.Equal("X", buffer.GetScreenLineText(0).TrimEnd());
    }

    [Fact]
    public void UnknownDcsTypeIsCountedWithoutPrinting()
    {
        var buffer = new AnsiTerminalBuffer(32, 3);

        buffer.Process("\u001bP1;2!zpayload\u001b\\X");

        Assert.Equal(1, buffer.UnknownDcsSequenceCount);
        Assert.Equal("X", buffer.GetScreenLineText(0).TrimEnd());
    }

    [Fact]
    public void SixelDcsIsConsumedWithoutCountingAsUnknown()
    {
        var buffer = new AnsiTerminalBuffer(32, 3);

        buffer.Process("\u001bPq#0;2;0;0;0-!200~\u001b\\X");

        Assert.Equal(0, buffer.UnknownDcsSequenceCount);
        Assert.Equal("X", buffer.GetScreenLineText(0).TrimEnd());
    }

    [Fact]
    public void PrimaryDeviceAttributesOmitsSixelAttribute()
    {
        var buffer = new AnsiTerminalBuffer(32, 3);
        string? emitted = null;
        buffer.InputSequenceGenerated += (_, text) => emitted = text;

        buffer.Process("[c");

        // VT220 with 132-column (1) and ANSI color (22); Sixel (attribute 4) must be absent.
        Assert.Equal("[?62;1;22c", emitted);
    }

    [Fact]
    public void Sgr5MarksSegmentAsBlinkingAndSgr25ClearsIt()
    {
        var buffer = new AnsiTerminalBuffer(32, 3);

        buffer.Process("[5mA[25mB");

        AnsiTerminalBuffer.TerminalRenderSnapshot snapshot = buffer.CreateRenderSnapshot(showCursor: false);
        // The blink attribute breaks the run, so "A" and "B" are separate segments.
        Assert.True(snapshot.Lines[0].Segments[0].Blink);
        Assert.Equal("A", snapshot.Lines[0].Segments[0].Text);
        Assert.False(snapshot.Lines[0].Segments[1].Blink);
        Assert.Equal("B", snapshot.Lines[0].Segments[1].Text);
    }

    [Fact]
    public void Sgr0ResetsBlinkAttribute()
    {
        var buffer = new AnsiTerminalBuffer(32, 3);

        buffer.Process("[5mA[0mB");

        AnsiTerminalBuffer.TerminalRenderSnapshot snapshot = buffer.CreateRenderSnapshot(showCursor: false);
        Assert.True(snapshot.Lines[0].Segments[0].Blink);
        Assert.False(snapshot.Lines[0].Segments[1].Blink);
    }

    [Fact]
    public void Xtwinops22And23PushAndPopWindowTitle()
    {
        var buffer = new AnsiTerminalBuffer(32, 3);

        buffer.Process("]2;First");
        buffer.Process("[22t");            // push "First"
        buffer.Process("]2;Second"); // now showing "Second"
        Assert.Equal("Second", buffer.WindowTitle);

        buffer.Process("[23t");            // pop → restore "First"
        Assert.Equal("First", buffer.WindowTitle);
    }

    [Fact]
    public void Xtwinops23PopWithEmptyStackLeavesTitleUnchanged()
    {
        var buffer = new AnsiTerminalBuffer(32, 3);

        buffer.Process("]2;OnlyTitle");
        buffer.Process("[23t"); // pop with nothing pushed is a no-op

        Assert.Equal("OnlyTitle", buffer.WindowTitle);
    }

    [Fact]
    public void WindowTitleStackIsNestedLifo()
    {
        var buffer = new AnsiTerminalBuffer(32, 3);

        buffer.Process("]2;A");
        buffer.Process("[22t");
        buffer.Process("]2;B");
        buffer.Process("[22t");
        buffer.Process("]2;C");

        buffer.Process("[23t");
        Assert.Equal("B", buffer.WindowTitle);
        buffer.Process("[23t");
        Assert.Equal("A", buffer.WindowTitle);
    }
}
