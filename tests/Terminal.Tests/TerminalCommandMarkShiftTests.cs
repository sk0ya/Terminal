using System.Text;

using Terminal.Buffer;
using Terminal.Tabs;

namespace Terminal.Tests;

/// <summary>OSC 133 marks follow their lines when lines leave the head of the buffer.</summary>
public sealed class TerminalCommandMarkShiftTests
{
    private const char Esc = (char)0x1b;
    private const char Bel = (char)0x07;

    private static string Osc(string body) => $"{Esc}]{body}{Bel}";

    [Fact]
    public void ShiftUpMovesMarksAndDropsThoseThatLeft()
    {
        var coordinator = new TerminalCommandNavigationCoordinator();
        coordinator.Observe(ShellCommandZoneType.PromptStart, 2);
        coordinator.Observe(ShellCommandZoneType.PromptStart, 10);
        coordinator.Observe(ShellCommandZoneType.CommandStart, 11);

        Assert.True(coordinator.ShiftUp(5));

        Assert.Equal([5], coordinator.PromptLines);
        Assert.Equal(new TerminalCommandMark(5, 6, Executed: false), Assert.Single(coordinator.Commands));
        Assert.False(coordinator.ShiftUp(0));
    }

    [Fact]
    public void VersionChangesOnlyWhenTheMarksDo()
    {
        var coordinator = new TerminalCommandNavigationCoordinator();
        int start = coordinator.Version;

        coordinator.Observe(ShellCommandZoneType.PromptStart, 3);
        int afterPrompt = coordinator.Version;
        Assert.NotEqual(start, afterPrompt);

        // A prompt redraw on the same line changes nothing.
        coordinator.Observe(ShellCommandZoneType.PromptStart, 3);
        Assert.Equal(afterPrompt, coordinator.Version);

        coordinator.Observe(ShellCommandZoneType.CommandDone, 4, 1, DateTime.UtcNow);
        Assert.NotEqual(afterPrompt, coordinator.Version);

        int beforeShift = coordinator.Version;
        coordinator.ShiftUp(0);
        Assert.Equal(beforeShift, coordinator.Version);
        coordinator.ShiftUp(1);
        Assert.NotEqual(beforeShift, coordinator.Version);
    }

    [Fact]
    public void ClearingTheScrollbackKeepsLaterMarksOnTheirLines()
    {
        StaTestRunner.Run(() =>
        {
            var view = new TerminalTabView("cmd.exe", Environment.CurrentDirectory);
            var output = new StringBuilder($"{Osc("133;A")}> {Osc("133;B")}old\r\n{Osc("133;C")}");
            for (int i = 0; i < 60; i++)
            {
                output.Append($"line {i}\r\n");
            }

            view.FeedOutputForTests(output.ToString());
            int scrollback = view.ScreenTopLineForTests;
            Assert.True(scrollback > 0);

            // cls: clear the scrollback, then a new prompt.
            view.FeedOutputForTests($"{Osc("133;D;0")}{Esc}[3J{Osc("133;A")}> {Osc("133;B")}");

            TerminalCommandNavigationCoordinator marks = view.CommandNavigationForTests;
            int prompt = Assert.Single(marks.PromptLines);
            Assert.Equal(">", GetLineText(view, prompt));
        });
    }

    private static string GetLineText(TerminalTabView view, int absoluteLine)
    {
        var buffer = (AnsiTerminalBuffer)typeof(TerminalTabView)
            .GetField("_terminalBuffer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(view)!;
        return buffer.GetPlainTextForAbsoluteLineRange(absoluteLine, absoluteLine + 1).TrimEnd();
    }
}
