using Terminal.Buffer;
using Terminal.Tabs;

namespace Terminal.Tests;

/// <summary>行が出た時刻：最初に書いたときに付き、書き直しでは変わらず、折り返し直しと再接続の再生で落ちない。</summary>
public sealed class TerminalLineTimeTests
{
    private static readonly long T1 = new DateTime(2026, 10, 19, 1, 0, 0, DateTimeKind.Utc).Ticks;
    private static readonly long T2 = new DateTime(2026, 10, 19, 1, 5, 0, DateTimeKind.Utc).Ticks;

    private static AnsiTerminalBuffer Buffer(short columns = 20, short rows = 5, long? now = null)
    {
        var clock = now ?? T1;
        var buffer = new AnsiTerminalBuffer(columns, rows);
        buffer.UtcTicksProvider = () => clock;
        return buffer;
    }

    [Fact]
    public void FirstWriteStampsTheLine_AndRewritesKeepIt()
    {
        long now = T1;
        var buffer = new AnsiTerminalBuffer(20, 5) { UtcTicksProvider = () => now };

        buffer.Process("first\r\n");
        now = T2;
        buffer.Process("\u001b[1A\rFIRST\r\nsecond");

        Assert.Equal(T1, buffer.GetLineWrittenAtTicks(0));
        Assert.Equal(T2, buffer.GetLineWrittenAtTicks(1));
        Assert.Equal(0, buffer.GetLineWrittenAtTicks(2));
    }

    [Fact]
    public void FindsFirstLineAtOrAfterTime()
    {
        long now = T1;
        var buffer = new AnsiTerminalBuffer(20, 5) { UtcTicksProvider = () => now };
        buffer.Process("a\r\nb\r\n");
        now = T2;
        buffer.Process("c\r\n");

        Assert.Equal(0, buffer.FindFirstLineWrittenAtOrAfter(T1));
        Assert.Equal(2, buffer.FindFirstLineWrittenAtOrAfter(T1 + 1));
        Assert.Null(buffer.FindFirstLineWrittenAtOrAfter(T2 + 1));
    }

    [Fact]
    public void ReflowKeepsTheLogicalLinesTime()
    {
        var buffer = Buffer(columns: 10);
        buffer.Process(new string('x', 15) + "\r\nshort");

        buffer.Resize(5, 5);

        Assert.All(buffer.AllLinesForTests.Where(l => l.Cells.Any(c => c.Text is { Length: > 0 } t && t != " ")),
            line => Assert.Equal(T1, line.WrittenAtUtcTicks));
    }

    [Fact]
    public void SnapshotReplayRestoresTimes()
    {
        long now = T1;
        var buffer = new AnsiTerminalBuffer(20, 5) { UtcTicksProvider = () => now };
        buffer.Process("old\r\n");
        now = T2;
        buffer.Process("new");

        var restored = new AnsiTerminalBuffer(20, 5) { UtcTicksProvider = () => 42 };
        restored.Process(buffer.CreateVtSnapshot([], null, null, null));

        Assert.Equal(T1, restored.GetLineWrittenAtTicks(0));
        Assert.Equal(T2, restored.GetLineWrittenAtTicks(1));
    }

    [Fact]
    public void TimeOscOutsideReplay_IsIgnored()
    {
        var buffer = Buffer();
        buffer.Process("x");   // 最初の出力のあとは再生の括りは効かない
        buffer.Process("\u001b]7770;t;123\u0007\r\nnext");

        Assert.Equal(T1, buffer.GetLineWrittenAtTicks(1));
    }

    [Fact]
    public void FormatsClockAndRelativeAge()
    {
        var written = new DateTime(2026, 10, 19, 1, 0, 0, DateTimeKind.Utc);

        string text = TerminalTabView.FormatLineTime(written, written.AddMinutes(3));

        Assert.EndsWith("に出力（3 分前）", text);
        Assert.Contains(written.ToLocalTime().ToString("HH:mm:ss"), text);
    }
}
