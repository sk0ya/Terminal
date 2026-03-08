using Terminal.Buffer;

namespace Terminal.Tests;

public sealed class AnsiTerminalBufferTests
{
    [Fact]
    public void ChtAndCbtFollowConfiguredTabStops()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);

        buffer.Process("\u001b[3G");
        buffer.Process("\u001b[2I");

        Assert.Equal(16, buffer.CursorColumn);

        buffer.Process("\u001b[Z");

        Assert.Equal(8, buffer.CursorColumn);
    }

    [Fact]
    public void TbcClearsCurrentTabStop()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);

        buffer.Process("\t");
        Assert.Equal(8, buffer.CursorColumn);

        buffer.Process("\u001b[0g");
        buffer.Process("\r\u001b[3G\t");

        Assert.Equal(16, buffer.CursorColumn);
    }

    [Fact]
    public void InsertModeShiftsExistingCellsToTheRight()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);

        buffer.Process("ABCD");
        buffer.Process("\r\u001b[2G\u001b[4hX");

        Assert.Equal("AXBCD", buffer.GetScreenLineText(0).TrimEnd());
    }

    [Fact]
    public void DecscusrUpdatesCursorShapeAndBlinkMode()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);

        buffer.Process("\u001b[4 q");

        Assert.Equal(TerminalCursorShape.Underline, buffer.CursorShape);
        Assert.False(buffer.CursorBlinkEnabled);

        buffer.Process("\u001b[5 q");

        Assert.Equal(TerminalCursorShape.Bar, buffer.CursorShape);
        Assert.True(buffer.CursorBlinkEnabled);
    }

    [Fact]
    public void MouseEncodingFallsBackToPreviouslyEnabledMode()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);

        buffer.Process("\u001b[?1005h");
        Assert.Equal(TerminalMouseEncoding.Utf8, buffer.MouseEncoding);

        buffer.Process("\u001b[?1006h");
        Assert.Equal(TerminalMouseEncoding.Sgr, buffer.MouseEncoding);

        buffer.Process("\u001b[?1006l");
        Assert.Equal(TerminalMouseEncoding.Utf8, buffer.MouseEncoding);

        buffer.Process("\u001b[?1015h");
        Assert.Equal(TerminalMouseEncoding.Urxvt, buffer.MouseEncoding);

        buffer.Process("\u001b[?1015l");
        Assert.Equal(TerminalMouseEncoding.Utf8, buffer.MouseEncoding);
    }

    [Fact]
    public void Osc8AppliesHyperlinksOnlyToSubsequentText()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);

        buffer.Process("\u001b]8;;https://example.com\u0007link\u001b]8;;\u0007 x");

        Assert.Equal("https://example.com", buffer.GetCellHyperlink(0, 0));
        Assert.Equal("https://example.com", buffer.GetCellHyperlink(0, 3));
        Assert.Null(buffer.GetCellHyperlink(0, 4));
        Assert.Null(buffer.GetCellHyperlink(0, 5));
    }

    [Fact]
    public void DeviceStatusReportEmitsCurrentCursorPosition()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);
        string? emitted = null;
        buffer.InputSequenceGenerated += (_, text) => emitted = text;

        buffer.Process("A\r\nBC");
        buffer.Process("\u001b[6n");

        Assert.Equal("\u001b[2;3R", emitted);
    }

    [Fact]
    public void DeviceAttributesRespondToPrimaryAndSecondaryQueries()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);
        var emitted = new List<string>();
        buffer.InputSequenceGenerated += (_, text) => emitted.Add(text);

        buffer.Process("\u001b[c");
        buffer.Process("\u001b[>c");

        Assert.Equal(new[] { "\u001b[?1;2c", "\u001b[>0;10;1c" }, emitted);
    }

    [Fact]
    public void Osc52ClipboardQueryRaisesSelectionTarget()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);
        string? requestedTarget = null;
        buffer.ClipboardQueryRequested += (_, target) => requestedTarget = target;

        buffer.Process("\u001b]52;s0;?\u0007");

        Assert.Equal("s0", requestedTarget);
    }

    [Fact]
    public void ZwjEmojiSequenceOccupiesSingleWideCluster()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);

        buffer.Process("👩\u200d💻");

        Assert.Equal(2, buffer.CursorColumn);
        Assert.Equal("👩\u200d💻", buffer.GetScreenLineText(0).TrimEnd());
    }

    [Fact]
    public void ZwjEmojiSequenceCanContinueAcrossProcessCalls()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);

        buffer.Process("👩");
        buffer.Process("\u200d💻");

        Assert.Equal(2, buffer.CursorColumn);
        Assert.Equal("👩\u200d💻", buffer.GetScreenLineText(0).TrimEnd());
    }

    [Fact]
    public void RegionalIndicatorPairOccupiesSingleWideCluster()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);

        buffer.Process("🇯");
        buffer.Process("🇵");

        Assert.Equal(2, buffer.CursorColumn);
        Assert.Equal("🇯🇵", buffer.GetScreenLineText(0).TrimEnd());
    }

    [Fact]
    public void DecPrivate1048RestoresSavedCursorPosition()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);
        string? emitted = null;
        buffer.InputSequenceGenerated += (_, text) => emitted = text;

        buffer.Process("\u001b[3;5H");
        buffer.Process("\u001b[?1048h");
        buffer.Process("\u001b[8;12H");
        buffer.Process("\u001b[?1048l");
        buffer.Process("\u001b[6n");

        Assert.Equal("\u001b[3;5R", emitted);
    }

    [Fact]
    public void DecPrivate1049RestoresPrimaryScreenAndCursor()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);
        string? emitted = null;
        buffer.InputSequenceGenerated += (_, text) => emitted = text;

        buffer.Process("main");
        buffer.Process("\u001b[2;4H");
        buffer.Process("\u001b[?1049h");
        buffer.Process("alt");
        buffer.Process("\u001b[8;8H");
        buffer.Process("\u001b[?1049l");
        buffer.Process("\u001b[6n");

        Assert.Equal("main", buffer.GetScreenLineText(0).TrimEnd());
        Assert.Equal("\u001b[2;4R", emitted);
    }

    [Fact]
    public void CreateRenderSnapshotReusesCombinedArrayWhenBufferIsUnchanged()
    {
        var buffer = new AnsiTerminalBuffer(8, 2);

        buffer.Process("A\r\nB\r\nC");

        AnsiTerminalBuffer.TerminalRenderSnapshot first = buffer.CreateRenderSnapshot(showCursor: false);
        AnsiTerminalBuffer.TerminalRenderSnapshot second = buffer.CreateRenderSnapshot(showCursor: false);

        Assert.Same(first.Lines, second.Lines);
    }

    [Fact]
    public void CreatePlainTextSnapshotIncludesScrollbackAndVisibleScreen()
    {
        var buffer = new AnsiTerminalBuffer(8, 2);

        buffer.Process("A\r\nB\r\nC");

        Assert.Equal("A" + Environment.NewLine + "B" + Environment.NewLine + "C", buffer.CreatePlainTextSnapshot());
    }

    [Fact]
    public void DecPrivate12ControlsCursorBlinking()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);

        buffer.Process("\u001b[?12l");
        Assert.False(buffer.CursorBlinkEnabled);

        buffer.Process("\u001b[?12h");
        Assert.True(buffer.CursorBlinkEnabled);
    }

    [Fact]
    public void DecPrivate1007TogglesAlternateScrollMode()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);

        buffer.Process("\u001b[?1007h");
        Assert.True(buffer.AlternateScrollEnabled);

        buffer.Process("\u001b[?1007l");
        Assert.False(buffer.AlternateScrollEnabled);
    }

    [Fact]
    public void RepRepeatsLastPrintedCluster()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);

        buffer.Process("A\u001b[3b");

        Assert.Equal("AAAA", buffer.GetScreenLineText(0).TrimEnd());
        Assert.Equal(4, buffer.CursorColumn);
    }

    [Fact]
    public void RepRepeatsWideGraphemeCluster()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);

        buffer.Process("👩\u200d💻\u001b[2b");

        Assert.Equal("👩\u200d💻👩\u200d💻👩\u200d💻", buffer.GetScreenLineText(0).TrimEnd());
        Assert.Equal(6, buffer.CursorColumn);
    }

    [Fact]
    public void DecstrSoftResetClearsTerminalModesWithoutClearingScreen()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);

        buffer.Process("text");
        buffer.Process("\u001b[?1h\u001b=\u001b[?25l\u001b[?12l\u001b[?1000h\u001b[?1007h\u001b[?2004h\u001b[2 q");
        buffer.Process("\u001b[!p");

        Assert.False(buffer.ApplicationCursorKeysEnabled);
        Assert.False(buffer.ApplicationKeypadEnabled);
        Assert.False(buffer.AlternateScrollEnabled);
        Assert.False(buffer.BracketedPasteEnabled);
        Assert.Equal(TerminalMouseTrackingMode.Off, buffer.MouseTrackingMode);
        Assert.True(buffer.CursorVisible);
        Assert.True(buffer.CursorBlinkEnabled);
        Assert.Equal(TerminalCursorShape.Block, buffer.CursorShape);
        Assert.Equal("text", buffer.GetScreenLineText(0).TrimEnd());
    }
}
