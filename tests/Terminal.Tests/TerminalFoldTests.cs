using System.Text;

using Terminal.Tabs;

namespace Terminal.Tests;

public sealed class TerminalFoldTests
{
    private const char Esc = (char)0x1b;
    private const char Bel = (char)0x07;

    private static string Osc(string body) => $"{Esc}]{body}{Bel}";

    [Fact]
    public void MapCollapsesEachFoldToOneLine()
    {
        // Buffer lines 0..19; folds hide [3,8) and [10,15).
        var map = new TerminalFoldMap([new TerminalFold(10, 15), new TerminalFold(3, 8)]);

        Assert.Equal(8, map.HiddenLineCount);
        Assert.Equal(2, map.ToDisplay(2));
        Assert.Equal(3, map.ToDisplay(3));
        Assert.Equal(3, map.ToDisplay(7));
        Assert.Equal(4, map.ToDisplay(8));
        Assert.Equal(6, map.ToDisplay(10));
        Assert.Equal(7, map.ToDisplay(15));

        Assert.Equal(2, map.ToBuffer(2, out TerminalFold? none));
        Assert.Null(none);
        Assert.Equal(3, map.ToBuffer(3, out TerminalFold? first));
        Assert.Equal(new TerminalFold(3, 8), first);
        Assert.Equal(8, map.ToBuffer(4));
        Assert.Equal(10, map.ToBuffer(6, out TerminalFold? second));
        Assert.Equal(new TerminalFold(10, 15), second);
        Assert.Equal(15, map.ToBuffer(7));
    }

    [Fact]
    public void ApplyReplacesFoldedLinesWithSummaries()
    {
        var map = new TerminalFoldMap([new TerminalFold(1, 4)]);
        string[] lines = ["a", "b", "c", "d", "e"];

        Assert.Equal(["a", "[3]", "e"], map.Apply(lines, fold => $"[{fold.LineCount}]"));
    }

    [Fact]
    public void TooShortAndOverlappingFoldsAreIgnored()
    {
        var map = new TerminalFoldMap([new TerminalFold(0, 1), new TerminalFold(2, 6), new TerminalFold(4, 9)]);
        Assert.Equal([new TerminalFold(2, 6)], map.Folds);
    }

    [Fact]
    public void FoldsOnlyTheScrollbackPartOfAFinishedCommand()
    {
        StaTestRunner.Run(() =>
        {
            var view = new TerminalTabView("cmd.exe", Environment.CurrentDirectory);
            var output = new StringBuilder();
            output.Append($"{Osc("133;A")}> {Osc("133;B")}big\r\n{Osc("133;C")}");
            for (int i = 0; i < 50; i++)
            {
                output.Append($"row {i}\r\n");
            }

            output.Append($"{Osc("133;D;0")}{Osc("133;A")}> {Osc("133;B")}next\r\n{Osc("133;C")}");
            for (int i = 0; i < 40; i++)
            {
                output.Append($"tail {i}\r\n");
            }

            view.FeedOutputForTests(output.ToString());

            // Line 0 is "> big", rows 1..50, the next prompt on 51.
            Assert.Equal(new TerminalFold(1, 51), view.ResolveFoldRange(10));
            Assert.True(view.FoldCommandOutputAt(10));
            view.RefreshFoldsForTests();
            Assert.Equal(1, view.FoldCount);
            Assert.Equal(49, view.FoldMapForTests.HiddenLineCount);

            // The second command's output is partly on the live screen (30 rows): only the part
            // already in the scrollback can fold, and nothing at all on the screen itself.
            Assert.Equal(new TerminalFold(52, 63), view.ResolveFoldRange(80));
            view.FeedOutputForTests($"{Osc("133;D;0")}{Osc("133;A")}> ");
            Assert.Null(new TerminalTabView("cmd.exe", Environment.CurrentDirectory).ResolveFoldRange(0));

            view.UnfoldAll();
            view.RefreshFoldsForTests();
            Assert.Equal(0, view.FoldCount);
        });
    }

    [Fact]
    public void SummaryTextIsAscii()
    {
        string text = TerminalTabView.FormatFoldSummary(1234);
        Assert.Equal("  [+] 1,234 lines folded (click to unfold)", text);
        Assert.All(text, c => Assert.True(c < 128));
    }
}
