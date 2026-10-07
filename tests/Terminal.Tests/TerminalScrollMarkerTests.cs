using Terminal.Rendering;
using Terminal.Tabs;

namespace Terminal.Tests;

public sealed class TerminalScrollMarkerTests
{
    private const char Esc = (char)0x1b;
    private const char Bel = (char)0x07;

    private static string Osc(string body) => $"{Esc}]{body}{Bel}";

    [Fact]
    public void LayoutPlacesLinesProportionallyOnTheTrack()
    {
        var ticks = TerminalScrollMarkerBar.Layout(
            [new TerminalScrollMark(0, TerminalScrollMarkKind.Prompt), new TerminalScrollMark(99, TerminalScrollMarkKind.Prompt)],
            totalLines: 100,
            trackTop: 17,
            trackHeight: 200);

        Assert.Equal(2, ticks.Count);
        Assert.Equal(18, ticks[0].Y);
        Assert.Equal(17 + 198 + 1, ticks[1].Y);
    }

    [Fact]
    public void LayoutKeepsTheStrongestKindPerRowAndDropsOutOfRangeLines()
    {
        var ticks = TerminalScrollMarkerBar.Layout(
            [
                new TerminalScrollMark(5000, TerminalScrollMarkKind.FailedCommand),
                new TerminalScrollMark(5001, TerminalScrollMarkKind.Prompt),
                new TerminalScrollMark(-1, TerminalScrollMarkKind.FindMatch),
                new TerminalScrollMark(10000, TerminalScrollMarkKind.FindMatch),
            ],
            totalLines: 10000,
            trackTop: 0,
            trackHeight: 100);

        TerminalScrollMarkTick tick = Assert.Single(ticks);
        Assert.Equal(TerminalScrollMarkKind.FailedCommand, tick.Kind);
    }

    [Fact]
    public void LayoutIsEmptyWithoutRoom()
    {
        Assert.Empty(TerminalScrollMarkerBar.Layout([new TerminalScrollMark(0, TerminalScrollMarkKind.Prompt)], 0, 0, 100));
        Assert.Empty(TerminalScrollMarkerBar.Layout([new TerminalScrollMark(0, TerminalScrollMarkKind.Prompt)], 10, 0, 1));
    }

    [Fact]
    public void MarksIncludeEveryPromptAndTheCommandLineOfFailures()
    {
        StaTestRunner.Run(() =>
        {
            var view = new TerminalTabView("cmd.exe", Environment.CurrentDirectory);
            view.FeedOutputForTests($"{Osc("133;A")}❯ {Osc("133;B")}ok\r\n{Osc("133;C")}{Osc("133;D;0")}");
            view.FeedOutputForTests($"{Osc("133;A")}❯ {Osc("133;B")}bad\r\n{Osc("133;C")}err\r\n{Osc("133;D;1")}");
            view.FeedOutputForTests($"{Osc("133;A")}❯ ");

            Assert.Equal(
                [
                    new TerminalScrollMark(0, TerminalScrollMarkKind.Prompt),
                    new TerminalScrollMark(1, TerminalScrollMarkKind.Prompt),
                    new TerminalScrollMark(1, TerminalScrollMarkKind.FailedCommand),
                    new TerminalScrollMark(3, TerminalScrollMarkKind.Prompt),
                ],
                view.ScrollMarksForTests);
        });
    }
}
