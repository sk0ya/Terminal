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
    public void ResizeKeepsVisibleContentAnchoredAtTopWhenGrowingRowsWithoutScrollback()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);

        buffer.Process("first\r\nsecond");
        buffer.Resize(32, 14);

        Assert.Equal("first", buffer.GetScreenLineText(0).TrimEnd());
        Assert.Equal("second", buffer.GetScreenLineText(1).TrimEnd());
    }

    [Fact]
    public void ResizeTruncatesVisibleContentWhenShrinkingColumnsWithoutReflow()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);

        buffer.Process("ABCDEFGHIJKLMNOPQRSTUVWX");
        buffer.Resize(20, 10);

        Assert.Equal("ABCDEFGHIJKLMNOPQRST", buffer.GetScreenLineText(0).TrimEnd());
        Assert.Equal(string.Empty, buffer.GetScreenLineText(1).TrimEnd());
    }

    [Fact]
    public void ResizeKeepsBottomRowsVisibleWhenPrimaryScreenShrinks()
    {
        var buffer = new AnsiTerminalBuffer(32, 12);
        var lines = new List<string>();
        for (int index = 1; index <= 12; index++)
        {
            lines.Add(index.ToString("00"));
        }

        buffer.Process(string.Join("\r\n", lines));
        buffer.Resize(32, 10);

        Assert.Equal("03", buffer.GetScreenLineText(0).TrimEnd());
        Assert.Equal("12", buffer.GetScreenLineText(9).TrimEnd());
    }

    [Fact]
    public void ResizeKeepsAlternateScreenAnchoredAtTop()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);

        buffer.Process("\u001b[?1049h");
        buffer.Process("alpha\r\nbeta");
        buffer.Resize(32, 14);

        Assert.Equal("alpha", buffer.GetScreenLineText(0).TrimEnd());
        Assert.Equal("beta", buffer.GetScreenLineText(1).TrimEnd());
    }

    [Fact]
    public void ExitAlternateScreenAfterResizeRestoresPrimaryScreenAtCurrentSize()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);

        buffer.Process("main");
        buffer.Process("\u001b[?1049h");
        buffer.Resize(40, 12);
        buffer.Process("\u001b[?1049l");

        AnsiTerminalBuffer.TerminalRenderSnapshot snapshot = buffer.CreateRenderSnapshot(showCursor: false);

        Assert.NotEmpty(snapshot.Lines);
        Assert.Equal("main", buffer.GetScreenLineText(0).TrimEnd());
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
    public void CreateRenderSnapshotOmitsScrollbackWhileAlternateScreenIsActive()
    {
        var buffer = new AnsiTerminalBuffer(8, 2);

        buffer.Process("A\r\nB");
        buffer.Process("\u001b[?1049h");
        buffer.Process("C");

        AnsiTerminalBuffer.TerminalRenderSnapshot snapshot = buffer.CreateRenderSnapshot(showCursor: false);

        Assert.Equal(10, snapshot.Lines.Length);
        Assert.Equal("C", string.Concat(snapshot.Lines[0].Segments.Select(segment => segment.Text)));
        Assert.Empty(snapshot.Lines[1].Segments);
    }

    [Fact]
    public void CreateRenderSnapshotKeepsFullViewportWhileAlternateScreenIsActive()
    {
        var buffer = new AnsiTerminalBuffer(20, 10);

        buffer.Process("\u001b[?1049h\u001b[10;1H");

        AnsiTerminalBuffer.TerminalRenderSnapshot snapshot = buffer.CreateRenderSnapshot(showCursor: false);

        Assert.Equal(10, snapshot.Lines.Length);
    }

    [Fact]
    public void DecPrivate2026TracksSynchronizedUpdateBoundaries()
    {
        var buffer = new AnsiTerminalBuffer(20, 10);

        bool endedInBeginBatch = buffer.Process("\u001b[?2026hframe");
        bool endedInEndBatch = buffer.Process("\u001b[?2026l");

        Assert.False(endedInBeginBatch);
        Assert.True(endedInEndBatch);
        Assert.False(buffer.SynchronizedUpdateActive);
    }

    [Fact]
    public void DecPrivate2026ReportsEndWhenBeginAndEndArriveTogether()
    {
        var buffer = new AnsiTerminalBuffer(20, 10);

        bool ended = buffer.Process("\u001b[?2026hframe\u001b[?2026l");

        Assert.True(ended);
        Assert.False(buffer.SynchronizedUpdateActive);
    }

    [Fact]
    public void ForceEndSynchronizedUpdateClearsUnclosedBoundary()
    {
        var buffer = new AnsiTerminalBuffer(20, 10);

        buffer.Process("\u001b[?2026hframe");

        Assert.True(buffer.SynchronizedUpdateActive);
        Assert.True(buffer.ForceEndSynchronizedUpdate());
        Assert.False(buffer.SynchronizedUpdateActive);
        Assert.False(buffer.ForceEndSynchronizedUpdate());
    }

    [Fact]
    public void ForceEndSynchronizedUpdateDoesNotExitAlternateScreen()
    {
        var buffer = new AnsiTerminalBuffer(20, 10);

        buffer.Process("primary");
        buffer.Process("\u001b[?1049h\u001b[?2026h");

        Assert.True(buffer.ForceEndSynchronizedUpdate());
        Assert.True(buffer.IsAlternateScreenActive);
        Assert.Equal(string.Empty, buffer.GetScreenLineText(0).TrimEnd());
    }

    [Fact]
    public void ForceExitAlternateScreenRestoresPrimaryScreen()
    {
        var buffer = new AnsiTerminalBuffer(20, 10);

        buffer.Process("primary");
        buffer.Process("\u001b[?1049h");
        buffer.Process("alternate");

        Assert.True(buffer.IsAlternateScreenActive);
        Assert.True(buffer.ForceExitAlternateScreen());
        Assert.False(buffer.IsAlternateScreenActive);
        Assert.False(buffer.ForceExitAlternateScreen());
        Assert.Equal("primary", buffer.GetScreenLineText(0).TrimEnd());
    }

    [Fact]
    public void ClaudeTitlePromotesRecentFullClearToSyntheticAlternateScreen()
    {
        var buffer = new AnsiTerminalBuffer(20, 10);

        buffer.Process("primary");
        buffer.Process("\u001b[2J\u001b[H\u001b]0;claude\u0007");
        buffer.Process("alternate");

        Assert.True(buffer.IsAlternateScreenActive);
        Assert.Equal("alternate", buffer.GetScreenLineText(0).TrimEnd());

        buffer.Process("\u001b]0;✳ Claude Code\u0007");
        buffer.Process("\u001b]0;\u0007");

        Assert.False(buffer.IsAlternateScreenActive);
        Assert.Equal("primary", buffer.GetScreenLineText(0).TrimEnd());
    }

    [Fact]
    public void NonClaudeTitleDiscardsSyntheticAlternateScreenCandidate()
    {
        var buffer = new AnsiTerminalBuffer(20, 10);

        buffer.Process("primary");
        buffer.Process("\u001b[2J\u001b[H\u001b]0;editor\u0007");
        buffer.Process("current");
        buffer.Process("\u001b]0;\u0007");

        Assert.False(buffer.IsAlternateScreenActive);
        Assert.Equal("current", buffer.GetScreenLineText(0).TrimEnd());
    }

    [Fact]
    public void ClaudeTitleWithoutRecentFullClearUsesCurrentScreenAsSyntheticBackup()
    {
        var buffer = new AnsiTerminalBuffer(20, 10);

        buffer.Process("primary");
        buffer.Process("\u001b]0;Claude Code\u0007");
        buffer.Process("\r\nalternate");

        Assert.True(buffer.IsAlternateScreenActive);
        Assert.Equal("alternate", buffer.GetScreenLineText(1).TrimEnd());

        buffer.Process("\u001b]0;\u0007");

        Assert.False(buffer.IsAlternateScreenActive);
        Assert.Equal("primary", buffer.GetScreenLineText(0).TrimEnd());
    }

    [Fact]
    public void ClaudeShellLaunchDoesNotLeaveUiOnPrimaryScreenAfterExit()
    {
        var buffer = new AnsiTerminalBuffer(80, 10);

        buffer.Process("PS C:\\Projects\\Terminal> & \"C:\\Users\\koya\\.local\\bin\\claude.exe\"\r\n");
        buffer.Process("\u001b]0;claude\u0007");
        buffer.Process("Claude Code UI\r\nMore UI");
        buffer.Process("\u001b]9;4;0;\u0007\r\n\u001b]0;\u0007\u001b[?25h");
        buffer.Process("PS C:\\Projects\\Terminal> ");

        Assert.False(buffer.IsAlternateScreenActive);
        Assert.DoesNotContain("Claude Code UI", buffer.CreatePlainTextSnapshot(), StringComparison.Ordinal);
        Assert.Equal("PS C:\\Projects\\Terminal>", buffer.GetScreenLineText(1).TrimEnd());
    }

    [Fact]
    public void C1CsiDecPrivate1049RestoresPrimaryScreen()
    {
        var buffer = new AnsiTerminalBuffer(20, 10);

        buffer.Process("primary");
        buffer.Process("\u009b?1049h");
        buffer.Process("alternate");
        buffer.Process("\u009b?1049l");

        Assert.False(buffer.IsAlternateScreenActive);
        Assert.Equal("primary", buffer.GetScreenLineText(0).TrimEnd());
    }

    [Fact]
    public void C1ControlsAreNotPrinted()
    {
        var buffer = new AnsiTerminalBuffer(20, 10);

        buffer.Process("A\u009cB");

        Assert.Equal("AB", buffer.GetScreenLineText(0).TrimEnd());
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

    [Fact]
    public void Sgr3And23ToggleItalicInRenderSnapshot()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);

        buffer.Process("[3mtext");
        AnsiTerminalBuffer.TerminalRenderSnapshot snapshot = buffer.CreateRenderSnapshot(showCursor: false);
        Assert.True(snapshot.Lines[0].Segments[0].Italic);

        buffer.Process("\r[23mnormal");
        snapshot = buffer.CreateRenderSnapshot(showCursor: false);
        Assert.False(snapshot.Lines[0].Segments[0].Italic);
    }

    [Fact]
    public void Sgr9And29ToggleStrikethroughInRenderSnapshot()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);

        buffer.Process("[9mtext");
        AnsiTerminalBuffer.TerminalRenderSnapshot snapshot = buffer.CreateRenderSnapshot(showCursor: false);
        Assert.True(snapshot.Lines[0].Segments[0].Strikethrough);

        buffer.Process("\r[29mnormal");
        snapshot = buffer.CreateRenderSnapshot(showCursor: false);
        Assert.False(snapshot.Lines[0].Segments[0].Strikethrough);
    }

    [Fact]
    public void Sgr2DimDarkensForegroundColorInRenderSnapshot()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);

        buffer.Process("[37mfull");
        AnsiTerminalBuffer.TerminalRenderSnapshot fullSnapshot = buffer.CreateRenderSnapshot(showCursor: false);
        System.Windows.Media.Color fullFg = fullSnapshot.Lines[0].Segments[0].Foreground;

        buffer.Process("\r[2mdim");
        AnsiTerminalBuffer.TerminalRenderSnapshot dimSnapshot = buffer.CreateRenderSnapshot(showCursor: false);
        System.Windows.Media.Color dimFg = dimSnapshot.Lines[0].Segments[0].Foreground;

        Assert.True(dimFg.R < fullFg.R || dimFg.G < fullFg.G || dimFg.B < fullFg.B,
            "dim foreground should be darker than normal foreground");
    }

    [Fact]
    public void Sgr8InvisibleMakesForegroundMatchBackground()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);

        buffer.Process("[8mhidden");
        AnsiTerminalBuffer.TerminalRenderSnapshot snapshot = buffer.CreateRenderSnapshot(showCursor: false);
        AnsiTerminalBuffer.TerminalRenderSegmentSnapshot seg = snapshot.Lines[0].Segments[0];

        Assert.Equal(seg.Background, seg.Foreground);
    }

    [Fact]
    public void Sgr22TurnsOffBothBoldAndDim()
    {
        var dimBuffer = new AnsiTerminalBuffer(32, 10);
        dimBuffer.Process("[37m[1;2mtext");
        System.Windows.Media.Color dimFg = dimBuffer.CreateRenderSnapshot(showCursor: false).Lines[0].Segments[0].Foreground;

        var normalBuffer = new AnsiTerminalBuffer(32, 10);
        normalBuffer.Process("[37m[1;2m[22mtext");
        AnsiTerminalBuffer.TerminalRenderSnapshot normalSnapshot = normalBuffer.CreateRenderSnapshot(showCursor: false);

        Assert.False(normalSnapshot.Lines[0].Segments[0].Bold);
        System.Windows.Media.Color normalFg = normalSnapshot.Lines[0].Segments[0].Foreground;
        Assert.True(normalFg.R > dimFg.R || normalFg.G > dimFg.G || normalFg.B > dimFg.B,
            "foreground after SGR 22 should be brighter than when dim was active");
    }

    [Fact]
    public void Sgr5And25ToggleBlinkWithoutCrash()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);

        buffer.Process("[5mfast");
        buffer.Process("\r[6mslow");
        buffer.Process("\r[25moff");

        AnsiTerminalBuffer.TerminalRenderSnapshot snapshot = buffer.CreateRenderSnapshot(showCursor: false);
        Assert.NotEmpty(snapshot.Lines[0].Segments);
    }
    [Fact]
    public void DecrqmReportsSetAndResetStatesForKnownModes()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);
        var responses = new List<string>();
        buffer.InputSequenceGenerated += (_, text) => responses.Add(text);

        buffer.Process("[?25$p");
        Assert.Equal("[?25;1$y", responses[^1]);

        buffer.Process("[?25l");
        buffer.Process("[?25$p");
        Assert.Equal("[?25;2$y", responses[^1]);

        buffer.Process("[?1049h");
        buffer.Process("[?1049$p");
        Assert.Equal("[?1049;1$y", responses[^1]);

        buffer.Process("[?1049l");
        buffer.Process("[?1049$p");
        Assert.Equal("[?1049;2$y", responses[^1]);
    }

    [Fact]
    public void DecrqmReturnsZeroForUnrecognizedMode()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);
        string? emitted = null;
        buffer.InputSequenceGenerated += (_, text) => emitted = text;

        buffer.Process("[?9999$p");

        Assert.Equal("[?9999;0$y", emitted);
    }

    [Fact]
    public void DecrqmReports2026SynchronizedUpdateState()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);
        var responses = new List<string>();
        buffer.InputSequenceGenerated += (_, text) => responses.Add(text);

        buffer.Process("[?2026$p");
        Assert.Equal("[?2026;2$y", responses[^1]);

        buffer.Process("[?2026h");
        buffer.Process("[?2026$p");
        Assert.Equal("[?2026;1$y", responses[^1]);
    }

    [Fact]
    public void XtwinopsReportsTerminalDimensions()
    {
        var buffer = new AnsiTerminalBuffer(80, 24);
        string? emitted = null;
        buffer.InputSequenceGenerated += (_, text) => emitted = text;

        buffer.Process("[18t");

        Assert.Equal("[8;24;80t", emitted);
    }

    [Fact]
    public void XtwinopsReportsWindowTitle()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);
        string? emitted = null;
        buffer.InputSequenceGenerated += (_, text) => emitted = text;

        buffer.Process("]2;MyTitle");
        buffer.Process("[21t");

        Assert.Contains("MyTitle", emitted);
    }

    [Fact]
    public void OscQueryForegroundColorRespondsWithRgbSpec()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);
        string? emitted = null;
        buffer.InputSequenceGenerated += (_, text) => emitted = text;

        buffer.Process("]10;?");

        Assert.NotNull(emitted);
        Assert.StartsWith("]10;rgb:", emitted);
    }

    [Fact]
    public void OscQueryBackgroundColorRespondsWithRgbSpec()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);
        string? emitted = null;
        buffer.InputSequenceGenerated += (_, text) => emitted = text;

        buffer.Process("]11;?");

        Assert.NotNull(emitted);
        Assert.StartsWith("]11;rgb:", emitted);
    }

    [Fact]
    public void OscQueryFgAndBgColorsAreDifferent()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);
        var responses = new List<string>();
        buffer.InputSequenceGenerated += (_, text) => responses.Add(text);

        buffer.Process("]10;?");
        buffer.Process("]11;?");

        Assert.Equal(2, responses.Count);
        Assert.NotEqual(responses[0], responses[1]);
    }

    [Fact]
    public void XtversionRespondsWithDcsTerminalIdentification()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);
        string? emitted = null;
        buffer.InputSequenceGenerated += (_, text) => emitted = text;

        buffer.Process("[>0q");

        Assert.NotNull(emitted);
        Assert.StartsWith("P>|", emitted);
        Assert.EndsWith("\\", emitted);
    }

    [Fact]
    public void CsiPrivateSDoesNotSaveCursorState()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);
        string? emitted = null;
        buffer.InputSequenceGenerated += (_, text) => emitted = text;

        buffer.Process("[5;10H");
        buffer.Process("[s");
        buffer.Process("[1;1H");
        buffer.Process("[?1s");
        buffer.Process("[2;2H");
        buffer.Process("[u");
        buffer.Process("[6n");

        Assert.Equal("[5;10R", emitted);
    }
    [Fact]
    public void CsiPrivateUDoesNotRestoreCursorState()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);
        string? emitted = null;
        buffer.InputSequenceGenerated += (_, text) => emitted = text;

        buffer.Process("[5;10H");
        buffer.Process("[s");
        buffer.Process("[1;1H");
        buffer.Process("[?1u");
        buffer.Process("[6n");

        Assert.Equal("[1;1R", emitted);
    }

    [Fact]
    public void DecscnmReversesScreenColorsInRenderSnapshot()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);

        buffer.Process("A");
        AnsiTerminalBuffer.TerminalRenderSnapshot normal = buffer.CreateRenderSnapshot(showCursor: false);
        System.Windows.Media.Color normalFg = normal.Lines[0].Segments[0].Foreground;
        System.Windows.Media.Color normalBg = normal.Lines[0].Segments[0].Background;

        buffer.Process("[?5h");
        buffer.Process("A");
        AnsiTerminalBuffer.TerminalRenderSnapshot reversed = buffer.CreateRenderSnapshot(showCursor: false);
        System.Windows.Media.Color reversedFg = reversed.Lines[0].Segments[0].Foreground;
        System.Windows.Media.Color reversedBg = reversed.Lines[0].Segments[0].Background;

        Assert.Equal(normalFg, reversedBg);
        Assert.Equal(normalBg, reversedFg);
    }

    [Fact]
    public void DecscnmCancelledByResetRestoresNormalColors()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);

        buffer.Process("A");
        AnsiTerminalBuffer.TerminalRenderSnapshot normal = buffer.CreateRenderSnapshot(showCursor: false);
        System.Windows.Media.Color normalFg = normal.Lines[0].Segments[0].Foreground;

        buffer.Process("[?5h[?5l");
        buffer.Process("A");
        AnsiTerminalBuffer.TerminalRenderSnapshot restored = buffer.CreateRenderSnapshot(showCursor: false);
        System.Windows.Media.Color restoredFg = restored.Lines[0].Segments[0].Foreground;

        Assert.Equal(normalFg, restoredFg);
    }

    [Fact]
    public void DecscnmReportedByDecrqm()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);
        var responses = new List<string>();
        buffer.InputSequenceGenerated += (_, text) => responses.Add(text);

        buffer.Process("[?5$p");
        Assert.Equal("[?5;2$y", responses[^1]);

        buffer.Process("[?5h");
        buffer.Process("[?5$p");
        Assert.Equal("[?5;1$y", responses[^1]);
    }

    [Fact]
    public void XtsaveAndXtrestoreRoundTripsPrivateModes()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);

        buffer.Process("[?1h[?7l[?2004h");
        Assert.True(buffer.ApplicationCursorKeysEnabled);
        Assert.True(buffer.BracketedPasteEnabled);

        buffer.Process("[?1;7;2004s");

        buffer.Process("[?1l[?2004l");
        Assert.False(buffer.ApplicationCursorKeysEnabled);
        Assert.False(buffer.BracketedPasteEnabled);

        buffer.Process("[?1;7;2004r");

        Assert.True(buffer.ApplicationCursorKeysEnabled);
        Assert.True(buffer.BracketedPasteEnabled);
    }

    [Fact]
    public void XtrestoreIgnoresModesThatWereNeverSaved()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);

        buffer.Process("[?1h");

        buffer.Process("[?7r");

        Assert.True(buffer.ApplicationCursorKeysEnabled);
        Assert.False(buffer.FocusReportingEnabled);
    }

    // Phase 4: additional mouse mode verification via DECRQM

    [Fact]
    public void DecrqmReportsX10MouseTrackingMode()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);
        var responses = new List<string>();
        buffer.InputSequenceGenerated += (_, text) => responses.Add(text);

        buffer.Process("[?1000$p");
        Assert.Equal("[?1000;2$y", responses[^1]);

        buffer.Process("[?1000h");
        buffer.Process("[?1000$p");
        Assert.Equal("[?1000;1$y", responses[^1]);

        buffer.Process("[?1000l");
        buffer.Process("[?1000$p");
        Assert.Equal("[?1000;2$y", responses[^1]);
    }

    [Fact]
    public void DecrqmReportsButtonEventMouseTrackingMode()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);
        var responses = new List<string>();
        buffer.InputSequenceGenerated += (_, text) => responses.Add(text);

        buffer.Process("[?1002h");
        buffer.Process("[?1002$p");
        Assert.Equal("[?1002;1$y", responses[^1]);

        buffer.Process("[?1002l");
        buffer.Process("[?1002$p");
        Assert.Equal("[?1002;2$y", responses[^1]);
    }

    [Fact]
    public void DecrqmReportsAnyEventMouseTrackingMode()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);
        var responses = new List<string>();
        buffer.InputSequenceGenerated += (_, text) => responses.Add(text);

        buffer.Process("[?1003h");
        buffer.Process("[?1003$p");
        Assert.Equal("[?1003;1$y", responses[^1]);

        buffer.Process("[?1003l");
        buffer.Process("[?1003$p");
        Assert.Equal("[?1003;2$y", responses[^1]);
    }

    [Fact]
    public void DecrqmReportsSgrMouseEncodingMode()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);
        var responses = new List<string>();
        buffer.InputSequenceGenerated += (_, text) => responses.Add(text);

        buffer.Process("[?1006h");
        buffer.Process("[?1006$p");
        Assert.Equal("[?1006;1$y", responses[^1]);

        buffer.Process("[?1006l");
        buffer.Process("[?1006$p");
        Assert.Equal("[?1006;2$y", responses[^1]);
    }

    [Fact]
    public void AnyEventMouseTrackingDisableResetsToOff()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);

        buffer.Process("[?1000h");
        Assert.Equal(TerminalMouseTrackingMode.X10, buffer.MouseTrackingMode);

        buffer.Process("[?1003h");
        Assert.Equal(TerminalMouseTrackingMode.AnyEvent, buffer.MouseTrackingMode);

        buffer.Process("[?1003l");
        Assert.Equal(TerminalMouseTrackingMode.Off, buffer.MouseTrackingMode);
    }

    [Fact]
    public void FocusReportingToggledByDecPrivate1004()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);

        buffer.Process("[?1004h");
        Assert.True(buffer.FocusReportingEnabled);

        buffer.Process("[?1004l");
        Assert.False(buffer.FocusReportingEnabled);
    }

    [Fact]
    public void DisablingX10MouseDoesNotKillButtonEventMode()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);

        buffer.Process("[?1002h");
        Assert.Equal(TerminalMouseTrackingMode.ButtonEvent, buffer.MouseTrackingMode);

        buffer.Process("[?1000l");
        Assert.Equal(TerminalMouseTrackingMode.ButtonEvent, buffer.MouseTrackingMode);
    }

    [Fact]
    public void DisablingButtonEventMouseDoesNotKillX10Mode()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);

        buffer.Process("[?1000h");
        Assert.Equal(TerminalMouseTrackingMode.X10, buffer.MouseTrackingMode);

        buffer.Process("[?1002l");
        Assert.Equal(TerminalMouseTrackingMode.X10, buffer.MouseTrackingMode);
    }

    [Fact]
    public void XtsaveRestoreDeccomDoesNotMoveCursorWhenModeUnchanged()
    {
        var buffer = new AnsiTerminalBuffer(20, 10);

        buffer.Process("[5;5H");
        buffer.Process("[?6s");
        buffer.Process("[?6r");

        Assert.Equal(4, buffer.CursorRow);
        Assert.Equal(4, buffer.CursorColumn);
    }

    // Phase 6: scroll region and VT compatibility regression tests (vim / less / htop)

    [Fact]
    public void ScrollRegionRestrictsScrollingToDefinedRange()
    {
        var buffer = new AnsiTerminalBuffer(20, 6);

        buffer.Process("line1\r\nline2\r\nline3\r\n");
        buffer.Process("[2;4r");
        buffer.Process("[4;1H");
        buffer.Process("line4\n");

        Assert.Equal("line1", buffer.GetScreenLineText(0).TrimEnd());
        Assert.Equal("line3", buffer.GetScreenLineText(1).TrimEnd());
        Assert.Equal("line4", buffer.GetScreenLineText(2).TrimEnd());
        Assert.Equal(string.Empty, buffer.GetScreenLineText(3).TrimEnd());
    }

    [Fact]
    public void InsertLinesShiftsContentDownWithinScrollRegion()
    {
        var buffer = new AnsiTerminalBuffer(20, 6);

        buffer.Process("A\r\nB\r\nC\r\nD");
        buffer.Process("[1;4r");
        buffer.Process("[2;1H");
        buffer.Process("[2L");

        Assert.Equal("A", buffer.GetScreenLineText(0).TrimEnd());
        Assert.Equal(string.Empty, buffer.GetScreenLineText(1).TrimEnd());
        Assert.Equal(string.Empty, buffer.GetScreenLineText(2).TrimEnd());
        Assert.Equal("B", buffer.GetScreenLineText(3).TrimEnd());
    }

    [Fact]
    public void DeleteLinesShiftsContentUpWithinScrollRegion()
    {
        var buffer = new AnsiTerminalBuffer(20, 6);

        buffer.Process("A\r\nB\r\nC\r\nD");
        buffer.Process("[1;4r");
        buffer.Process("[1;1H");
        buffer.Process("[2M");

        Assert.Equal("C", buffer.GetScreenLineText(0).TrimEnd());
        Assert.Equal("D", buffer.GetScreenLineText(1).TrimEnd());
        Assert.Equal(string.Empty, buffer.GetScreenLineText(2).TrimEnd());
        Assert.Equal(string.Empty, buffer.GetScreenLineText(3).TrimEnd());
    }

    [Fact]
    public void ReverseIndexScrollsDownAtScrollRegionTop()
    {
        var buffer = new AnsiTerminalBuffer(20, 6);

        buffer.Process("A\r\nB\r\nC");
        buffer.Process("[1;3r");
        buffer.Process("[1;1H");
        buffer.Process("M");

        Assert.Equal(string.Empty, buffer.GetScreenLineText(0).TrimEnd());
        Assert.Equal("A", buffer.GetScreenLineText(1).TrimEnd());
        Assert.Equal("B", buffer.GetScreenLineText(2).TrimEnd());
    }

    [Fact]
    public void ReverseIndexMovesUpWhenNotAtScrollRegionTop()
    {
        var buffer = new AnsiTerminalBuffer(20, 6);

        buffer.Process("A\r\nB\r\nC");
        buffer.Process("[1;4r");
        buffer.Process("[3;1H");
        buffer.Process("M");

        Assert.Equal(1, buffer.CursorRow);
        Assert.Equal("A", buffer.GetScreenLineText(0).TrimEnd());
    }

    [Fact]
    public void OriginModeConstrainsCursorPositionToScrollRegion()
    {
        var buffer = new AnsiTerminalBuffer(20, 10);

        buffer.Process("[3;7r");
        buffer.Process("[?6h");
        buffer.Process("[1;1H");

        Assert.Equal(2, buffer.CursorRow);
        Assert.Equal(0, buffer.CursorColumn);
    }

    [Fact]
    public void OriginModeClampsCursorToRegionBoundary()
    {
        var buffer = new AnsiTerminalBuffer(20, 10);

        buffer.Process("[3;5r");
        buffer.Process("[?6h");
        buffer.Process("[99;99H");

        Assert.Equal(4, buffer.CursorRow);
        Assert.Equal(19, buffer.CursorColumn);
    }

    [Fact]
    public void EraseCharactersBlanksCellsWithoutMovingCursor()
    {
        var buffer = new AnsiTerminalBuffer(20, 5);

        buffer.Process("ABCDE");
        buffer.Process("[1;3H");
        buffer.Process("[2X");

        Assert.Equal("AB  E", buffer.GetScreenLineText(0).TrimEnd());
        Assert.Equal(2, buffer.CursorColumn);
    }

    [Fact]
    public void DeleteCharactersShiftsCellsLeft()
    {
        var buffer = new AnsiTerminalBuffer(20, 5);

        buffer.Process("ABCDE");
        buffer.Process("[1;2H");
        buffer.Process("[2P");

        Assert.Equal("ADE", buffer.GetScreenLineText(0).TrimEnd());
        Assert.Equal(1, buffer.CursorColumn);
    }

    [Fact]
    public void InsertCharactersShiftsCellsRight()
    {
        var buffer = new AnsiTerminalBuffer(20, 5);

        buffer.Process("ABCDE");
        buffer.Process("[1;2H");
        buffer.Process("[2@");

        Assert.Equal("A  BCDE", buffer.GetScreenLineText(0).TrimEnd());
        Assert.Equal(1, buffer.CursorColumn);
    }

    [Fact]
    public void AutoWrapDisabledKeepsCursorAtLastColumn()
    {
        var buffer = new AnsiTerminalBuffer(20, 5);

        buffer.Process("[?7l");
        buffer.Process("ABCDEFGHIJKLMNOPQRSTU");

        Assert.Equal("ABCDEFGHIJKLMNOPQRSU", buffer.GetScreenLineText(0).TrimEnd());
        Assert.Equal(19, buffer.CursorColumn);
        Assert.Equal(0, buffer.CursorRow);
    }

    [Fact]
    public void DecSpecialGraphicsRendersBoxDrawingCharacters()
    {
        var buffer = new AnsiTerminalBuffer(20, 5);

        buffer.Process("(0lqk(B");

        string line = buffer.GetScreenLineText(0).TrimEnd();
        Assert.Equal("┌─┐", line);
    }

    [Fact]
    public void G1CharsetSwitchActivatesDecSpecialGraphics()
    {
        var buffer = new AnsiTerminalBuffer(20, 5);

        buffer.Process(")0mqj");

        string line = buffer.GetScreenLineText(0).TrimEnd();
        Assert.Equal("└─┘", line);
    }

    [Fact]
    public void ClearDisplayMode1ClearsFromTopToCursor()
    {
        var buffer = new AnsiTerminalBuffer(20, 5);

        buffer.Process("AAA\r\nBBB\r\nCCC");
        buffer.Process("[2;2H");
        buffer.Process("[1J");

        Assert.Equal(string.Empty, buffer.GetScreenLineText(0).TrimEnd());
        Assert.Equal("  B", buffer.GetScreenLineText(1).TrimEnd());
        Assert.Equal("CCC", buffer.GetScreenLineText(2).TrimEnd());
    }

    [Fact]
    public void ClearLineMode1ClearsFromStartToCursor()
    {
        var buffer = new AnsiTerminalBuffer(20, 5);

        buffer.Process("ABCDE");
        buffer.Process("[1;3H");
        buffer.Process("[1K");

        Assert.Equal("   DE", buffer.GetScreenLineText(0).TrimEnd());
    }

    [Fact]
    public void SetCursorRowPositionedByAbsoluteRow()
    {
        var buffer = new AnsiTerminalBuffer(20, 10);

        buffer.Process("[5d");

        Assert.Equal(4, buffer.CursorRow);
        Assert.Equal(0, buffer.CursorColumn);
    }

    [Fact]
    public void CursorUpDownLeftRightRespectBoundaries()
    {
        var buffer = new AnsiTerminalBuffer(20, 10);

        buffer.Process("[1;1H");
        buffer.Process("[A");
        Assert.Equal(0, buffer.CursorRow);

        buffer.Process("[10;20H");
        buffer.Process("[B");
        Assert.Equal(9, buffer.CursorRow);

        buffer.Process("[10;20H");
        buffer.Process("[C");
        Assert.Equal(19, buffer.CursorColumn);

        buffer.Process("[1;1H");
        buffer.Process("[D");
        Assert.Equal(0, buffer.CursorColumn);
    }

    [Fact]
    public void ApplicationKeypadToggledByEscapeSequences()
    {
        var buffer = new AnsiTerminalBuffer(20, 10);

        buffer.Process("=");
        Assert.True(buffer.ApplicationKeypadEnabled);

        buffer.Process(">");
        Assert.False(buffer.ApplicationKeypadEnabled);
    }

    [Fact]
    public void OscTerminatedByStringTerminatorIsProcessed()
    {
        var buffer = new AnsiTerminalBuffer(20, 10);

        buffer.Process("]2;TestTitle\\");

        Assert.Equal("TestTitle", buffer.WindowTitle);
    }

    [Fact]
    public void DecrqmReportsOriginModeState()
    {
        var buffer = new AnsiTerminalBuffer(20, 10);
        var responses = new List<string>();
        buffer.InputSequenceGenerated += (_, text) => responses.Add(text);

        buffer.Process("[?6$p");
        Assert.Equal("[?6;2$y", responses[^1]);

        buffer.Process("[?6h");
        buffer.Process("[?6$p");
        Assert.Equal("[?6;1$y", responses[^1]);
    }

    [Fact]
    public void DecrqmReportsAutoWrapModeState()
    {
        var buffer = new AnsiTerminalBuffer(20, 10);
        var responses = new List<string>();
        buffer.InputSequenceGenerated += (_, text) => responses.Add(text);

        buffer.Process("[?7$p");
        Assert.Equal("[?7;1$y", responses[^1]);

        buffer.Process("[?7l");
        buffer.Process("[?7$p");
        Assert.Equal("[?7;2$y", responses[^1]);
    }

    [Fact]
    public void DecrqmReportsApplicationCursorKeysState()
    {
        var buffer = new AnsiTerminalBuffer(20, 10);
        var responses = new List<string>();
        buffer.InputSequenceGenerated += (_, text) => responses.Add(text);

        buffer.Process("[?1$p");
        Assert.Equal("[?1;2$y", responses[^1]);

        buffer.Process("[?1h");
        buffer.Process("[?1$p");
        Assert.Equal("[?1;1$y", responses[^1]);
    }

    [Fact]
    public void ScrollUpCsiSShiftsRegionContent()
    {
        var buffer = new AnsiTerminalBuffer(20, 6);

        buffer.Process("A\r\nB\r\nC\r\nD");
        buffer.Process("[1;4r");
        buffer.Process("[2S");

        Assert.Equal("C", buffer.GetScreenLineText(0).TrimEnd());
        Assert.Equal("D", buffer.GetScreenLineText(1).TrimEnd());
        Assert.Equal(string.Empty, buffer.GetScreenLineText(2).TrimEnd());
        Assert.Equal(string.Empty, buffer.GetScreenLineText(3).TrimEnd());
    }

    [Fact]
    public void ScrollDownCsiTShiftsRegionContent()
    {
        var buffer = new AnsiTerminalBuffer(20, 6);

        buffer.Process("A\r\nB\r\nC\r\nD");
        buffer.Process("[1;4r");
        buffer.Process("[2T");

        Assert.Equal(string.Empty, buffer.GetScreenLineText(0).TrimEnd());
        Assert.Equal(string.Empty, buffer.GetScreenLineText(1).TrimEnd());
        Assert.Equal("A", buffer.GetScreenLineText(2).TrimEnd());
        Assert.Equal("B", buffer.GetScreenLineText(3).TrimEnd());
    }

    [Fact]
    public void Sgr53SetsOverlineAnd55ClearsIt()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);

        buffer.Process("[53mX[55mY");

        var snapshot = buffer.CreateRenderSnapshot(showCursor: false);
        bool xOverline = snapshot.Lines[0].Segments.FirstOrDefault(s => s.Text.Contains('X')).Overline;
        bool yOverline = snapshot.Lines[0].Segments.FirstOrDefault(s => s.Text.Contains('Y')).Overline;

        Assert.True(xOverline);
        Assert.False(yOverline);
    }

    [Fact]
    public void Osc7FiresCurrentDirectoryChangedWithLocalPath()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);
        string? receivedPath = null;
        buffer.CurrentDirectoryChanged += (_, path) => receivedPath = path;

        buffer.Process("]7;file:///C:/Users/user/project");

        Assert.NotNull(receivedPath);
        Assert.Contains("project", receivedPath);
    }

    [Fact]
    public void Osc7WithBarePathFiresWithRawValue()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);
        string? receivedPath = null;
        buffer.CurrentDirectoryChanged += (_, path) => receivedPath = path;

        buffer.Process("]7;/home/user/project");

        Assert.Equal("/home/user/project", receivedPath);
    }


    [Fact]
    public void Osc4SetsAnsiPaletteColorAndAffectsRendering()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);

        buffer.Process("[31mA");
        var snapshotBefore = buffer.CreateRenderSnapshot(showCursor: false);
        var colorBefore = snapshotBefore.Lines[0].Segments[0].Foreground;

        buffer.Process("]4;1;rgb:ff/00/00");

        buffer.Process("\r[2K[31mA");
        var snapshotAfter = buffer.CreateRenderSnapshot(showCursor: false);
        var colorAfter = snapshotAfter.Lines[0].Segments[0].Foreground;

        Assert.NotEqual(colorBefore, colorAfter);
        Assert.Equal(255, colorAfter.R);
        Assert.Equal(0, colorAfter.G);
        Assert.Equal(0, colorAfter.B);
    }

    [Fact]
    public void Osc4QueryEmitsCurrentPaletteColor()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);
        string? response = null;
        buffer.InputSequenceGenerated += (_, seq) => response = seq;

        buffer.Process("]4;1;?");

        Assert.NotNull(response);
        Assert.StartsWith("]4;1;rgb:", response);
    }

    [Fact]
    public void Osc4ResetOnHardReset()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);

        buffer.Process("]4;0;rgb:ff/00/00");
        buffer.Process("c");

        string? response = null;
        buffer.InputSequenceGenerated += (_, seq) => response = seq;
        buffer.Process("]4;0;?");

        Assert.NotNull(response);
        Assert.DoesNotContain("ff/00/00", response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Osc4HashColorFormatIsSupported()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);

        buffer.Process("]4;2;#00ff80[32mA");
        var snapshot = buffer.CreateRenderSnapshot(showCursor: false);
        var color = snapshot.Lines[0].Segments[0].Foreground;

        Assert.Equal(0, color.R);
        Assert.Equal(255, color.G);
        Assert.Equal(128, color.B);
    }

    [Fact]
    public void Osc133PromptStartFiresShellZoneEvent()
    {
        var buffer = new AnsiTerminalBuffer(80, 24);
        var events = new List<ShellCommandZoneEventArgs>();
        buffer.ShellCommandZoneReceived += (_, e) => events.Add(e);

        buffer.Process("]133;A");

        Assert.Single(events);
        Assert.Equal(ShellCommandZoneType.PromptStart, events[0].ZoneType);
    }

    [Fact]
    public void Osc133CommandExecutedFiresShellZoneEvent()
    {
        var buffer = new AnsiTerminalBuffer(80, 24);
        var events = new List<ShellCommandZoneEventArgs>();
        buffer.ShellCommandZoneReceived += (_, e) => events.Add(e);

        buffer.Process("]133;C");

        Assert.Single(events);
        Assert.Equal(ShellCommandZoneType.CommandExecuted, events[0].ZoneType);
    }

    [Fact]
    public void Osc133CommandDoneWithExitCodeFiresShellZoneEvent()
    {
        var buffer = new AnsiTerminalBuffer(80, 24);
        var events = new List<ShellCommandZoneEventArgs>();
        buffer.ShellCommandZoneReceived += (_, e) => events.Add(e);

        buffer.Process("]133;D;42");

        Assert.Single(events);
        Assert.Equal(ShellCommandZoneType.CommandDone, events[0].ZoneType);
        Assert.Equal(42, events[0].ExitCode);
    }

    [Fact]
    public void Osc633FiresSameEventsAsOsc133()
    {
        var buffer = new AnsiTerminalBuffer(80, 24);
        var events = new List<ShellCommandZoneEventArgs>();
        buffer.ShellCommandZoneReceived += (_, e) => events.Add(e);

        buffer.Process("]633;A");
        buffer.Process("]633;D;0");

        Assert.Equal(2, events.Count);
        Assert.Equal(ShellCommandZoneType.PromptStart, events[0].ZoneType);
        Assert.Equal(ShellCommandZoneType.CommandDone, events[1].ZoneType);
        Assert.Equal(0, events[1].ExitCode);
    }

    [Fact]
    public void Osc133AbsoluteLineIncludesScrollback()
    {
        var buffer = new AnsiTerminalBuffer(10, 5);

        for (int i = 0; i < 8; i++)
        {
            buffer.Process("line\n");
        }

        int absoluteLine = -1;
        buffer.ShellCommandZoneReceived += (_, e) => absoluteLine = e.AbsoluteLine;
        buffer.Process("]133;A");

        Assert.True(absoluteLine > 0);
        Assert.Equal(buffer.ScrollbackLineCount + buffer.CursorRow, absoluteLine);
    }

    [Fact]
    public void KittyPushFlagsUpdatesCurrentFlags()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);

        buffer.Process("[>1u");

        Assert.Equal(1, buffer.KittyKeyboardFlags);
    }

    [Fact]
    public void KittyPushAndPopRestoresPreviousFlags()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);

        buffer.Process("[>1u");
        buffer.Process("[>3u");
        buffer.Process("[<u");

        Assert.Equal(1, buffer.KittyKeyboardFlags);
    }

    [Fact]
    public void KittyPopWithCountPopsMultipleLevels()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);

        buffer.Process("[>1u");
        buffer.Process("[>3u");
        buffer.Process("[>7u");
        buffer.Process("[<2u");

        Assert.Equal(1, buffer.KittyKeyboardFlags);
    }

    [Fact]
    public void KittyQueryRespondsWithCurrentFlags()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);
        string? emitted = null;
        buffer.InputSequenceGenerated += (_, text) => emitted = text;

        buffer.Process("[>5u");
        buffer.Process("[?u");

        Assert.Equal("[?5u", emitted);
    }

    [Fact]
    public void KittySetFlagsDirectlyWithModeSet()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);

        buffer.Process("[>3u");
        buffer.Process("[=5;1u");

        Assert.Equal(5, buffer.KittyKeyboardFlags);
    }

    [Fact]
    public void KittySetFlagsWithOrMode()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);

        buffer.Process("[>1u");
        buffer.Process("[=4;2u");

        Assert.Equal(5, buffer.KittyKeyboardFlags);
    }

    [Fact]
    public void KittySetFlagsWithAndNotMode()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);

        buffer.Process("[>7u");
        buffer.Process("[=2;3u");

        Assert.Equal(5, buffer.KittyKeyboardFlags);
    }

    [Fact]
    public void KittyFlagsResetOnHardReset()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);

        buffer.Process("[>3u");
        buffer.Process("c");

        Assert.Equal(0, buffer.KittyKeyboardFlags);
    }

    [Fact]
    public void KittyFlagsResetOnSoftReset()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);

        buffer.Process("[>7u");
        buffer.Process("[!p");

        Assert.Equal(0, buffer.KittyKeyboardFlags);
    }

    [Fact]
    public void KittyFlagsAreSavedAndRestoredWithAlternateScreen()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);

        buffer.Process("[>3u");
        buffer.Process("[?1049h");
        buffer.Process("[>15u");

        Assert.Equal(15, buffer.KittyKeyboardFlags);

        buffer.Process("[?1049l");

        Assert.Equal(3, buffer.KittyKeyboardFlags);
    }

    [Fact]
    public void KittyQueryEmitsEscapePrefixedResponse()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);
        var responses = new List<string>();
        buffer.InputSequenceGenerated += (_, text) => responses.Add(text);

        buffer.Process("[>0u");
        buffer.Process("[?u");

        Assert.Single(responses);
        Assert.Equal("[?0u", responses[0]);
    }

    [Fact]
    public void KittyStackIsSavedAndRestoredWithAlternateScreen()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);

        // primary screen: push 1 then 3
        buffer.Process("[>1u");
        buffer.Process("[>3u");

        // enter alt-screen, push 7 and then 15 there
        buffer.Process("[?1049h");
        buffer.Process("[>7u");
        buffer.Process("[>15u");

        // pop one level in alt-screen: should fall back to 7
        buffer.Process("[<u");
        Assert.Equal(7, buffer.KittyKeyboardFlags);

        // exit alt-screen: flags and stack from primary screen are restored
        buffer.Process("[?1049l");
        Assert.Equal(3, buffer.KittyKeyboardFlags);

        // pop one level: should restore the 1 that was pushed on primary screen
        buffer.Process("[<u");
        Assert.Equal(1, buffer.KittyKeyboardFlags);
    }
}
