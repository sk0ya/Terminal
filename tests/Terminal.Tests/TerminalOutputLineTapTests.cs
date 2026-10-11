using Terminal.Buffer;
using Terminal.Tabs;

namespace Terminal.Tests;

/// <summary>
/// <see cref="TerminalTabView.SubscribeOutputLines"/>: finished logical lines, one at a time, while a
/// long-running command keeps the shell — and the end of the command that started after subscribing.
/// </summary>
public sealed class TerminalOutputLineTapTests
{
    private const char Esc = (char)0x1b;
    private const char Bel = (char)0x07;

    private static string Osc(string body) => $"{Esc}]{body}{Bel}";

    private sealed class Harness
    {
        public readonly AnsiTerminalBuffer Buffer;
        public readonly List<string> Lines = [];
        public readonly List<int?> Finished = [];
        public readonly TerminalOutputLineTap Tap;

        public Harness(short columns = 40, short rows = 6, int scrollbackLimit = 1000)
        {
            Buffer = new AnsiTerminalBuffer(columns, rows, scrollbackLimit: scrollbackLimit);
            Tap = new TerminalOutputLineTap(Lines.Add, Finished.Add);
            Buffer.ShellCommandZoneReceived += (_, e) => Tap.OnShellZone(Buffer, e);
        }

        public void Feed(string text)
        {
            Buffer.Process(text);
            Tap.Pump(Buffer);
        }
    }

    [Fact]
    public void FinishedLinesStreamAndTheCursorLineIsHeldBack()
    {
        var h = new Harness();
        h.Feed("PS> ");
        h.Tap.Start(h.Buffer);
        h.Feed("tsc --watch\r\n" + Osc("133;C"));
        h.Feed("Starting compilation in watch mode...\r\nsrc/a.ts(3,1): error TS1");

        Assert.Equal(["PS> tsc --watch", "Starting compilation in watch mode..."], h.Lines);

        h.Feed("005: ';' expected.\r\n");
        Assert.Equal("src/a.ts(3,1): error TS1005: ';' expected.", h.Lines[^1]);
        Assert.Empty(h.Finished);
    }

    [Fact]
    public void SoftWrappedRowsComeBackAsOneLine()
    {
        var h = new Harness(columns: 20);
        h.Tap.Start(h.Buffer);
        var longLine = "C:\\work\\src\\Program.cs(12,5): error CS1002: ; expected";
        h.Feed(longLine + "\r\nnext\r\n");

        Assert.Equal([longLine, "next"], h.Lines);
    }

    [Fact]
    public void EndOfTheCommandIsReportedAfterSettlingWithoutThePrompt()
    {
        var h = new Harness();
        h.Feed("PS> ");
        h.Tap.Start(h.Buffer);
        h.Feed("npm run watch\r\n" + Osc("133;C") + "built\r\n");
        // ConPTY: D and the next prompt arrive before the last of the text is painted.
        h.Feed(Osc("133;D;130") + Osc("133;A") + "last line\r\nPS> ");
        Assert.Empty(h.Finished);

        h.Tap.Settle(h.Buffer);
        Assert.Equal([130], h.Finished);
        Assert.Contains("last line", h.Lines);
        Assert.DoesNotContain(h.Lines, line => line.StartsWith("PS> ", StringComparison.Ordinal) && line != "PS> npm run watch");
        Assert.True(h.Tap.IsEnded);

        h.Feed("more\r\n");
        Assert.DoesNotContain("more", h.Lines);   // nothing after the end
    }

    [Fact]
    public void ACommandThatEndedBeforeSubscribingDoesNotEndTheTap()
    {
        var h = new Harness();
        h.Tap.Start(h.Buffer);
        h.Feed(Osc("133;D;0") + Osc("133;A") + "PS> ");   // the previous command's D (no C seen yet)
        h.Tap.Settle(h.Buffer);
        Assert.Empty(h.Finished);
        Assert.False(h.Tap.IsEnded);
    }

    [Fact]
    public void StreamingContinuesPastScrollbackEviction()
    {
        var h = new Harness(rows: 4, scrollbackLimit: 5);
        h.Tap.Start(h.Buffer);
        h.Buffer.Process(string.Join("\r\n", Enumerable.Range(1, 30).Select(i => $"line{i}")) + "\r\n");
        h.Tap.Pump(h.Buffer);

        // Lines that left the buffer before the tap could read them are gone; the rest arrive in order.
        Assert.Equal("line30", h.Lines[^1]);
        Assert.Equal(h.Lines.OrderBy(l => int.Parse(l[4..])), h.Lines);

        h.Feed("line31\r\n");
        Assert.Equal("line31", h.Lines[^1]);
        Assert.Single(h.Lines, "line31");
    }

    [Fact]
    public void ReflowSkipsAheadInsteadOfReplaying()
    {
        var h = new Harness();
        h.Tap.Start(h.Buffer);
        h.Feed("one\r\ntwo\r\n");
        // The view pumps before the resize and resyncs after it (RenumberOutputTaps).
        h.Tap.Pump(h.Buffer);
        h.Buffer.Resize(50, 6);
        h.Tap.Resync(h.Buffer);
        h.Feed("three\r\n");

        Assert.Equal(["one", "two", "three"], h.Lines);
    }

    [Fact]
    public void RenumberingWithoutAResyncNeverReplaysOldText()
    {
        var h = new Harness();
        h.Tap.Start(h.Buffer);
        h.Feed("one\r\n");
        h.Buffer.Resize(50, 6);
        h.Feed("two\r\nthree\r\n");

        Assert.Single(h.Lines, "one");   // at worst lines are skipped, never handed out twice
    }

    [Fact]
    public void AbortReportsTheEndWithoutAnExitCode()
    {
        var h = new Harness();
        h.Tap.Start(h.Buffer);
        h.Tap.Abort();
        Assert.Equal([(int?)null], h.Finished);
    }
}
