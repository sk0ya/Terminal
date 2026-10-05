using Terminal.Tabs;

namespace Terminal.Tests;

/// <summary>
/// <see cref="TerminalTabView.CommandOutputCaptured"/>: one command's output, cut after the terminal
/// settles because ConPTY forwards OSC 133 markers ahead of the text they belong to.
/// </summary>
public sealed class TerminalCommandOutputTests
{
    private const char Esc = (char)0x1b;
    private const char Bel = (char)0x07;
    private static readonly string Nl = Environment.NewLine;

    private static string Osc(string body) => $"{Esc}]{body}{Bel}";

    [Fact]
    public void LeadingEchoOfTheCommandIsDropped()
    {
        var lines = new List<string> { "PS C:\\work> dotnet test", "Passed: 3", "Failed: 1", "" };
        Assert.Equal("Passed: 3" + Nl + "Failed: 1",
            TerminalCommandOutputCoordinator.ExtractOutput(lines, "dotnet test", null));
    }

    [Fact]
    public void WrappedEchoAndTrailingNextCommandAreDropped()
    {
        var lines = new List<string>
        {
            "PS C:\\work> Write-Output alp", "ha; Write-Output beta", "alpha", "beta", "", "PS C:\\work> git status",
        };
        Assert.Equal("alpha" + Nl + "beta",
            TerminalCommandOutputCoordinator.ExtractOutput(lines, "Write-Output alpha; Write-Output beta", "git status"));
    }

    [Fact]
    public void EchoRepaintedOnAFreshRowIsDroppedToo()
    {
        // Seen with real pwsh: ConPTY repainted the echoed command at the prompt's column on a new row.
        var lines = new List<string>
        {
            "PS C:\\work> 1..2 | % { \"row $_\" }", "", "           1..2 | % { \"row $_\" }", "row 1", "row 2",
        };
        Assert.Equal("row 1" + Nl + "row 2",
            TerminalCommandOutputCoordinator.ExtractOutput(lines, "1..2 | % { \"row $_\" }", null));
    }

    [Fact]
    public void EchoWithStaleCellsAfterItIsDropped()
    {
        // Also seen with real pwsh: a partial repaint left cells of an earlier line after the echo.
        var lines = new List<string> { "PS C:\\work> Write-Output x     put beta", "x" };
        Assert.Equal("x", TerminalCommandOutputCoordinator.ExtractOutput(lines, "Write-Output x", null));
    }

    [Fact]
    public void OutputRowsMerelyContainingAShortCommandAreKept()
    {
        // "Tools" contains "ls": neither the repeat pass nor a C that landed after the echo may take it.
        var lines = new List<string> { "PS C:\\Tools> ls", "", "    Directory: C:\\Tools", "", "tools.json" };
        Assert.Equal("    Directory: C:\\Tools" + Nl + Nl + "tools.json",
            TerminalCommandOutputCoordinator.ExtractOutput(lines, "ls", null));

        var withoutEcho = new List<string> { "    Directory: C:\\Tools", "", "tools.json" };
        Assert.Equal("    Directory: C:\\Tools" + Nl + Nl + "tools.json",
            TerminalCommandOutputCoordinator.ExtractOutput(withoutEcho, "ls", null));
    }

    [Fact]
    public void PreviousOutputTailMerelyContainingTheNextCommandIsKept()
    {
        var lines = new List<string> { "PS C:\\work> ls", "readme.md", "tools.json", "PS C:\\work> ls" };
        Assert.Equal("readme.md" + Nl + "tools.json",
            TerminalCommandOutputCoordinator.ExtractOutput(lines, "ls", "ls"));
    }

    [Theory]
    [InlineData("PS C:\\work> ls", true)]
    [InlineData("❯ls", true)]
    [InlineData("ls", true)]
    [InlineData("    Directory: C:\\Tools", false)]
    [InlineData("tools.json", false)]
    [InlineData("lsof", false)]
    public void EchoMatchesTheCommandOnlyAsAWholeRun(string row, bool expected)
    {
        Assert.Equal(expected, TerminalCommandOutputCoordinator.ContainsEcho(row, "ls", blankBefore: false));
    }

    [Fact]
    public void MultiLinePromptIsNotCapturedAsOutput()
    {
        StaTestRunner.Run(() =>
        {
            var view = new TerminalTabView("cmd.exe", Environment.CurrentDirectory);
            var captured = new List<ShellCommandOutputEventArgs>();
            view.CommandOutputCaptured += (_, e) => captured.Add(e);

            // The first prompt arrives with nothing else being painted, so its height is learned.
            view.FeedOutputForTests($"{Osc("133;A")}~/work\r\n❯ {Osc("133;B")}echo one\r\n{Osc("633;E;echo one")}{Osc("133;C")}");
            view.FeedOutputForTests($"one\r\n{Osc("133;D;0")}{Osc("133;A")}~/work\r\n❯ {Osc("133;B")}");
            view.SettleCommandOutputForTests();
            Assert.Equal("one", Assert.Single(captured).Output);

            // Later prompts' A lands ahead of output's tail (ConPTY), yet the prompt still isn't output.
            view.FeedOutputForTests($"echo two\r\n{Osc("633;E;echo two")}{Osc("133;C")}two\r\n{Osc("133;D;0")}{Osc("133;A")}");
            view.FeedOutputForTests("three\r\n~/work\r\n❯ ");
            view.SettleCommandOutputForTests();
            Assert.Equal("two" + Nl + "three", captured[1].Output);
        });
    }

    [Fact]
    public void OutputWithoutEchoIsKeptWhole()
    {
        var lines = new List<string> { "", "row 1", "row 2   ", "" };
        Assert.Equal("row 1" + Nl + "row 2", TerminalCommandOutputCoordinator.ExtractOutput(lines, "1..2", null));
    }

    [Fact]
    public void OutputIsCutAfterSettleEvenWhenTextArrivesAfterD()
    {
        StaTestRunner.Run(() =>
        {
            var view = new TerminalTabView("cmd.exe", Environment.CurrentDirectory);
            var captured = new List<ShellCommandOutputEventArgs>();
            view.CommandOutputCaptured += (_, e) => captured.Add(e);

            view.FeedOutputForTests($"{Osc("133;A")}PS> {Osc("133;B")}Write-Output alpha; Write-Output beta");
            // ConPTY order: C before the echo's newline, D/A/B before the last output and the prompt.
            view.FeedOutputForTests($"{Osc("633;E;Write-Output alpha\\x3b Write-Output beta")}{Osc("133;C")}\r\nalpha\r\n");
            view.FeedOutputForTests($"{Osc("133;D;0")}{Osc("133;A")}{Osc("133;B")}");
            Assert.Empty(captured);   // not cut at D

            view.FeedOutputForTests("beta\r\nPS> ");
            view.SettleCommandOutputForTests();

            var output = Assert.Single(captured);
            Assert.Equal("Write-Output alpha; Write-Output beta", output.CommandLine);
            Assert.Equal(0, output.ExitCode);
            Assert.Equal("alpha" + Nl + "beta", output.Output);
            Assert.False(output.HeadLost);

            // Settling again without a new command raises nothing.
            view.SettleCommandOutputForTests();
            Assert.Single(captured);
        });
    }

    [Fact]
    public void NextCommandStartingBeforeSettleCutsThePreviousOutput()
    {
        StaTestRunner.Run(() =>
        {
            var view = new TerminalTabView("cmd.exe", Environment.CurrentDirectory);
            var captured = new List<ShellCommandOutputEventArgs>();
            view.CommandOutputCaptured += (_, e) => captured.Add(e);

            view.FeedOutputForTests($"{Osc("133;A")}PS> {Osc("133;B")}echo one\r\n{Osc("633;E;echo one")}{Osc("133;C")}one\r\n");
            view.FeedOutputForTests($"{Osc("133;D;0")}{Osc("133;A")}PS> {Osc("133;B")}echo two\r\n");
            view.FeedOutputForTests($"{Osc("633;E;echo two")}{Osc("133;C")}two\r\n{Osc("133;D;0")}{Osc("133;A")}PS> ");

            Assert.Equal("one", Assert.Single(captured).Output);
            view.SettleCommandOutputForTests();
            Assert.Equal(2, captured.Count);
            Assert.Equal("echo two", captured[1].CommandLine);
            Assert.Equal("two", captured[1].Output);
        });
    }

    [Fact]
    public void DoneWithoutExecutedRaisesNothing()
    {
        StaTestRunner.Run(() =>
        {
            var view = new TerminalTabView("cmd.exe", Environment.CurrentDirectory);
            var captured = new List<ShellCommandOutputEventArgs>();
            view.CommandOutputCaptured += (_, e) => captured.Add(e);

            view.FeedOutputForTests($"{Osc("133;A")}{Osc("133;D")}PS> ");
            view.SettleCommandOutputForTests();
            Assert.Empty(captured);
        });
    }
}
