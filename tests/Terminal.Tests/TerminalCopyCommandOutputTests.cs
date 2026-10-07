using Terminal.Tabs;

namespace Terminal.Tests;

/// <summary>"Copy Command Output": captured outputs are kept without a subscriber and mapped back to lines.</summary>
public sealed class TerminalCopyCommandOutputTests
{
    private const char Esc = (char)0x1b;
    private const char Bel = (char)0x07;

    private static string Osc(string body) => $"{Esc}]{body}{Bel}";

    [Fact]
    public void OutputsAreKeptWithoutASubscriberAndFoundByAnyLineOfTheirCommand()
    {
        StaTestRunner.Run(() =>
        {
            var view = new TerminalTabView("cmd.exe", Environment.CurrentDirectory);

            // Lines: 0 prompt "echo one", 1 "one", 2 prompt "echo two", 3 "two", 4 "2b", 5 prompt.
            view.FeedOutputForTests($"{Osc("133;A")}❯ {Osc("133;B")}echo one\r\n{Osc("633;E;echo one")}{Osc("133;C")}");
            view.FeedOutputForTests($"one\r\n{Osc("133;D;0")}{Osc("133;A")}❯ {Osc("133;B")}");
            view.SettleCommandOutputForTests();
            view.FeedOutputForTests($"echo two\r\n{Osc("633;E;echo two")}{Osc("133;C")}two\r\n2b\r\n{Osc("133;D;0")}{Osc("133;A")}❯ ");
            view.SettleCommandOutputForTests();

            Assert.Equal(2, view.CapturedOutputsForTests.Count);
            Assert.Equal("one", view.FindCapturedOutputForLine(0)?.Output);
            Assert.Equal("one", view.FindCapturedOutputForLine(1)?.Output);
            Assert.Equal("two" + Environment.NewLine + "2b", view.FindCapturedOutputForLine(2)?.Output);
            Assert.Equal("two" + Environment.NewLine + "2b", view.FindCapturedOutputForLine(4)?.Output);
            Assert.Null(view.FindCapturedOutputForLine(5));
        });
    }

    [Theory]
    [InlineData("ls", "a\r\nb", "Copied output of 'ls' (2 lines).")]
    [InlineData(null, "a", "Copied command output (1 line).")]
    [InlineData("true", "", "Copied output of 'true' (0 lines).")]
    public void StatusDescribesWhatWasCopied(string? commandLine, string output, string expected)
    {
        Assert.Equal(expected, TerminalTabView.DescribeCopiedOutput(commandLine, output.Replace("\r\n", Environment.NewLine)));
    }
}
