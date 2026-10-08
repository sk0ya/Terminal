using Terminal.Buffer;

namespace Terminal.Tests;

/// <summary>
/// A snapshot fed into a fresh buffer of the same size must rebuild the same state. The strongest
/// cheap check is a fixed point: the restored buffer's own snapshot equals the original's, which
/// covers every cell, style, wrap flag, cursor position and mode the writer knows about.
/// </summary>
public sealed class TerminalVtSnapshotTests
{
    private const short Columns = 40;
    private const short Rows = 8;

    private static AnsiTerminalBuffer Restore(string snapshot, short columns = Columns, short rows = Rows)
    {
        var restored = new AnsiTerminalBuffer(columns, rows);
        restored.Process(snapshot);
        return restored;
    }

    private static string Snapshot(AnsiTerminalBuffer buffer, IReadOnlyList<TerminalShellMark>? marks = null) =>
        buffer.CreateVtSnapshot(marks ?? [], null, null, null);

    private static void AssertSameLines(AnsiTerminalBuffer expected, AnsiTerminalBuffer actual)
    {
        TerminalLine[] expectedLines = expected.AllLinesForTests.ToArray();
        TerminalLine[] actualLines = actual.AllLinesForTests.ToArray();
        Assert.Equal(expectedLines.Length, actualLines.Length);
        for (int line = 0; line < expectedLines.Length; line++)
        {
            Assert.Equal(expectedLines[line].IsWrapped, actualLines[line].IsWrapped);
            for (int column = 0; column < expectedLines[line].Cells.Length; column++)
            {
                TerminalCell want = expectedLines[line].Cells[column];
                TerminalCell got = actualLines[line].Cells[column];
                Assert.True(
                    want.Text == got.Text && want.Style == got.Style && want.IsContinuation == got.IsContinuation &&
                    want.Hyperlink?.Uri == got.Hyperlink?.Uri,
                    $"line {line} column {column}: want '{want.Text}' {want.Style}, got '{got.Text}' {got.Style}");
            }
        }
    }

    [Fact]
    public void ScrollbackScreenAndCursorRoundTrip()
    {
        var buffer = new AnsiTerminalBuffer(Columns, Rows);
        for (int index = 1; index <= 20; index++)
        {
            buffer.Process($"\u001b[3{index % 8}mline {index}\u001b[0m plain\r\n");
        }

        buffer.Process("PS C:\\> \u001b[1;4;38;2;10;20;30mtyped");

        AnsiTerminalBuffer restored = Restore(Snapshot(buffer));

        Assert.Equal(buffer.ScrollbackLineCount, restored.ScrollbackLineCount);
        Assert.Equal(buffer.CreatePlainTextSnapshot(), restored.CreatePlainTextSnapshot());
        Assert.Equal(buffer.CursorRow, restored.CursorRow);
        Assert.Equal(buffer.CursorColumn, restored.CursorColumn);
        AssertSameLines(buffer, restored);
        Assert.Equal(Snapshot(buffer), Snapshot(restored));
    }

    [Fact]
    public void PaletteColorsGoOutAsIndicesSoTheReaderThemeApplies()
    {
        var buffer = new AnsiTerminalBuffer(Columns, Rows);
        buffer.Process("\u001b[31mred\u001b[92mgreen\u001b[38;5;196mcube\u001b[38;2;1;2;3mrgb");

        string snapshot = Snapshot(buffer);

        Assert.Contains("\u001b[0;31mred", snapshot);
        Assert.Contains("\u001b[0;92mgreen", snapshot);
        Assert.Contains("38;5;196mcube", snapshot);
        Assert.Contains("38;2;1;2;3mrgb", snapshot);
    }

    [Fact]
    public void SoftWrappedLinesStayWrappedAndRewrapOnResize()
    {
        var buffer = new AnsiTerminalBuffer(Columns, Rows);
        buffer.Process(new string('x', Columns + 10) + "\r\nnext");

        AnsiTerminalBuffer restored = Restore(Snapshot(buffer));
        AssertSameLines(buffer, restored);

        restored.Resize(Columns + 20, Rows);
        Assert.Contains(new string('x', Columns + 10), restored.CreatePlainTextSnapshot());
    }

    [Fact]
    public void WideCharactersAndHyperlinksRoundTrip()
    {
        var buffer = new AnsiTerminalBuffer(Columns, Rows);
        buffer.Process("日本語テキスト \u001b]8;;https://example.com\u0007link\u001b]8;;\u0007 after");

        AnsiTerminalBuffer restored = Restore(Snapshot(buffer));

        AssertSameLines(buffer, restored);
        Assert.Equal("https://example.com", restored.GetCellHyperlink(0, 15));
    }

    [Fact]
    public void AlternateScreenComesBackAndLeavingItRestoresThePrimary()
    {
        var buffer = new AnsiTerminalBuffer(Columns, Rows);
        buffer.Process("history 1\r\nhistory 2\r\nPS> vim");
        buffer.Process("\u001b[?1049h\u001b[H~\r\n~\r\n\u001b[7m-- INSERT --\u001b[0m\u001b[2;5H");

        AnsiTerminalBuffer restored = Restore(Snapshot(buffer));

        Assert.True(restored.IsAlternateScreenActive);
        Assert.Equal(buffer.CreatePlainTextSnapshot(), restored.CreatePlainTextSnapshot());
        Assert.Equal((1, 4), (restored.CursorRow, restored.CursorColumn));

        buffer.Process("\u001b[?1049l");
        restored.Process("\u001b[?1049l");
        Assert.False(restored.IsAlternateScreenActive);
        Assert.Equal(buffer.CreatePlainTextSnapshot(), restored.CreatePlainTextSnapshot());
        Assert.Equal((buffer.CursorRow, buffer.CursorColumn), (restored.CursorRow, restored.CursorColumn));
    }

    [Fact]
    public void ModesAreRestored()
    {
        var buffer = new AnsiTerminalBuffer(Columns, Rows);
        buffer.Process("\u001b[?1h\u001b[?2004h\u001b[?1002h\u001b[?1006h\u001b[?1004h\u001b[?25l\u001b[5 q\u001b]2;my title\u0007");

        AnsiTerminalBuffer restored = Restore(Snapshot(buffer));

        Assert.True(restored.ApplicationCursorKeysEnabled);
        Assert.True(restored.BracketedPasteEnabled);
        Assert.Equal(TerminalMouseTrackingMode.ButtonEvent, restored.MouseTrackingMode);
        Assert.Equal(TerminalMouseEncoding.Sgr, restored.MouseEncoding);
        Assert.True(restored.FocusReportingEnabled);
        Assert.False(restored.CursorVisible);
        Assert.Equal(TerminalCursorShape.Bar, restored.CursorShape);
        Assert.Equal("my title", restored.WindowTitle);
    }

    [Fact]
    public void ScrollRegionAndSavedCursorAreRestored()
    {
        var buffer = new AnsiTerminalBuffer(Columns, Rows);
        buffer.Process("\u001b[3;4H\u001b7\u001b[2;6r\u001b[5;10H");

        AnsiTerminalBuffer restored = Restore(Snapshot(buffer));

        Assert.Equal((4, 9), (restored.CursorRow, restored.CursorColumn));
        Assert.Equal(Snapshot(buffer), Snapshot(restored));
        restored.Process("\u001b8");
        Assert.Equal((2, 3), (restored.CursorRow, restored.CursorColumn));
    }

    [Fact]
    public void ShellMarksAreReplayedInsideTheReplayBracket()
    {
        var buffer = new AnsiTerminalBuffer(Columns, Rows);
        var marks = new List<TerminalShellMark>
        {
            new(ShellCommandZoneType.PromptStart, 0, null),
            new(ShellCommandZoneType.CommandStart, 0, null),
            new(ShellCommandZoneType.CommandExecuted, 0, null),
            new(ShellCommandZoneType.CommandDone, 2, 1),
            new(ShellCommandZoneType.PromptStart, 2, null),
        };
        buffer.Process("PS> dir\r\noutput\r\nPS> ");

        var restored = new AnsiTerminalBuffer(Columns, Rows);
        var seen = new List<(ShellCommandZoneType Type, int Line, int? Exit, bool Replaying)>();
        restored.ShellCommandZoneReceived += (_, e) => seen.Add((e.ZoneType, e.AbsoluteLine, e.ExitCode, restored.IsReplaying));
        restored.Process(buffer.CreateVtSnapshot(marks, null, null, null));

        Assert.Equal(
            [
                (ShellCommandZoneType.PromptStart, 0, (int?)null, true),
                (ShellCommandZoneType.CommandStart, 0, null, true),
                (ShellCommandZoneType.CommandExecuted, 0, null, true),
                (ShellCommandZoneType.CommandDone, 2, 1, true),
                (ShellCommandZoneType.PromptStart, 2, null, true),
            ],
            seen);
        Assert.False(restored.IsReplaying);
    }

    [Fact]
    public void RunningCommandLineGoesJustBeforeItsExecutedMark()
    {
        var buffer = new AnsiTerminalBuffer(Columns, Rows);
        buffer.Process("PS> sleep 100\r\n");
        var marks = new List<TerminalShellMark>
        {
            new(ShellCommandZoneType.PromptStart, 0, null),
            new(ShellCommandZoneType.CommandExecuted, 1, null),
        };

        var restored = new AnsiTerminalBuffer(Columns, Rows);
        var order = new List<string>();
        restored.ShellCommandLineReceived += (_, command) => order.Add("E:" + command);
        restored.ShellCommandZoneReceived += (_, e) => order.Add(e.ZoneType.ToString());
        restored.Process(buffer.CreateVtSnapshot(marks, @"C:\work dir", null, "sleep 100; echo a\\b"));

        Assert.Equal(["PromptStart", "E:sleep 100; echo a\\b", "CommandExecuted"], order);
    }

    [Fact]
    public void CurrentDirectoryIsReported()
    {
        var buffer = new AnsiTerminalBuffer(Columns, Rows);
        var restored = new AnsiTerminalBuffer(Columns, Rows);
        string? directory = null;
        restored.CurrentDirectoryChanged += (_, path) => directory = path;

        restored.Process(buffer.CreateVtSnapshot([], @"C:\work dir\日本", null, null));

        Assert.Equal(@"C:/work dir/日本", directory?.Replace('\\', '/'));
    }

    [Fact]
    public void HeadlessTerminalKeepsMarksAcrossAWidthChange()
    {
        var terminal = new HeadlessTerminal(Columns, Rows);
        terminal.Process("\u001b]133;A\u0007PS> " + new string('y', 60) + "\r\n");
        terminal.Process("\u001b]133;A\u0007PS> \u001b]133;B\u0007");

        terminal.Resize(Columns + 40, Rows);

        var restored = new AnsiTerminalBuffer(Columns + 40, Rows);
        var lines = new List<int>();
        restored.ShellCommandZoneReceived += (_, e) => lines.Add(e.AbsoluteLine);
        restored.Process(terminal.CreateVtSnapshot());

        // The long first prompt no longer wraps, so the second prompt moved up a row with it.
        Assert.Equal([0, 1, 1], lines);
    }

    [Fact]
    public void SavedCursorKeepsItsPen()
    {
        var buffer = new AnsiTerminalBuffer(Columns, Rows);
        buffer.Process("\u001b[3;4H\u001b[1;31m\u001b7\u001b[0m\u001b[5;10H");

        AnsiTerminalBuffer restored = Restore(Snapshot(buffer));
        buffer.Process("\u001b8X");
        restored.Process("\u001b8X");

        AssertSameLines(buffer, restored);
        Assert.True(buffer.AllLinesForTests.Skip(2).First().Cells[3].Style.Bold);
    }

    [Fact]
    public void TheReplayBracketCountsOnlyAsTheFirstOutput()
    {
        var buffer = new AnsiTerminalBuffer(Columns, Rows);
        buffer.Process("PS> ");

        // A program printing the bytes later (a file being cat'ed) must not silence the tab.
        buffer.Process("\u001b]7770;begin\u0007");

        Assert.False(buffer.IsReplaying);
    }

    [Fact]
    public void ResetEndsAReplay()
    {
        var buffer = new AnsiTerminalBuffer(Columns, Rows);
        buffer.Process("\u001b]7770;begin\u0007");
        Assert.True(buffer.IsReplaying);

        buffer.Process("\u001bc");

        Assert.False(buffer.IsReplaying);
    }

    [Fact]
    public void HeadlessTerminalKeepsMarksAcrossAResizeInsideTheAlternateScreen()
    {
        var terminal = new HeadlessTerminal(Columns, Rows);
        terminal.Process("\u001b]133;A\u0007PS> " + new string('y', 60) + "\r\n");
        terminal.Process("\u001b]133;A\u0007PS> \u001b]133;B\u0007");

        // vim: the primary screen is rewrapped only when it is left, at the size of that moment.
        terminal.Process("\u001b[?1049hVIM");
        terminal.Resize(Columns + 20, Rows);
        terminal.Resize(Columns + 40, Rows);
        terminal.Process("\u001b[?1049l");

        var restored = new AnsiTerminalBuffer(Columns + 40, Rows);
        var lines = new List<int>();
        restored.ShellCommandZoneReceived += (_, e) => lines.Add(e.AbsoluteLine);
        restored.Process(terminal.CreateVtSnapshot());

        Assert.Equal([0, 1, 1], lines);
    }

    [Fact]
    public void ARedrawnCommandGetsNoMadeUpDuration()
    {
        var navigation = new Terminal.Tabs.TerminalCommandNavigationCoordinator();
        navigation.Observe(ShellCommandZoneType.PromptStart, 0, null, nowUtc: null);
        navigation.Observe(ShellCommandZoneType.CommandExecuted, 0, null, nowUtc: null);

        // The command was still running at the re-attach and finishes live.
        navigation.Observe(ShellCommandZoneType.CommandDone, 3, 0, DateTime.UtcNow);

        var command = Assert.Single(navigation.Commands);
        Assert.True(command.Done);
        Assert.Null(command.ExecutedAtUtc);
        Assert.Null(command.Duration);
    }

    [Fact]
    public void HeadlessTerminalAnswersQueries()
    {
        var terminal = new HeadlessTerminal(Columns, Rows);
        var responses = new List<string>();
        terminal.ResponseGenerated += (_, text) => responses.Add(text);

        terminal.Process("ab\u001b[6n");

        Assert.Equal(["\u001b[1;3R"], responses);
    }
}
