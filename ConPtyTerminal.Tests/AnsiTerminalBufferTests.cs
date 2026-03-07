namespace ConPtyTerminal.Tests;

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
}
