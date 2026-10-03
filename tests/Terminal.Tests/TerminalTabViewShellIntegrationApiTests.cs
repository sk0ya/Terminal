using System.Runtime.ExceptionServices;
using System.Threading;

using Terminal.Tabs;

namespace Terminal.Tests;

public sealed class TerminalTabViewShellIntegrationApiTests
{
    [Fact]
    public void ShellIntegrationInjectionEnabledRoundTrips()
    {
        StaTestRunner.Run(() =>
        {
            // The constructor seeds the value from the saved app settings, so only
            // the setter/getter contract is asserted here.
            var view = new TerminalTabView("cmd.exe", Environment.CurrentDirectory);

            view.ShellIntegrationInjectionEnabled = false;
            Assert.False(view.ShellIntegrationInjectionEnabled);

            view.ShellIntegrationInjectionEnabled = true;
            Assert.True(view.ShellIntegrationInjectionEnabled);
        });
    }

    [Fact]
    public void IsStatusBarVisibleRoundTrips()
    {
        StaTestRunner.Run(() =>
        {
            var view = new TerminalTabView("cmd.exe", Environment.CurrentDirectory);

            view.IsStatusBarVisible = true;
            Assert.True(view.IsStatusBarVisible);

            view.IsStatusBarVisible = false;
            Assert.False(view.IsStatusBarVisible);
        });
    }

    [Fact]
    public void AutoFocusOnStartDefaultsTrueAndRoundTrips()
    {
        StaTestRunner.Run(() =>
        {
            var view = new TerminalTabView("cmd.exe", Environment.CurrentDirectory);

            Assert.True(view.AutoFocusOnStart);

            view.AutoFocusOnStart = false;
            Assert.False(view.AutoFocusOnStart);
        });
    }

    [Fact]
    public void IsShellIntegrationActiveIsFalseBeforeAnyMarkerArrives()
    {
        StaTestRunner.Run(() =>
        {
            var view = new TerminalTabView("cmd.exe", Environment.CurrentDirectory);

            Assert.False(view.IsShellIntegrationActive);
        });
    }

    [Fact]
    public void ShellCommandActivityMapsOscMarkersToPhases()
    {
        // (marker, expected phase, expected exit code). A single view feeds every marker:
        // constructing one view per case on parallel STA threads races WPF's BAML loading
        // (System.IO.Packaging is not thread-safe), which made a Theory version flaky.
        var cases = new (string Marker, ShellCommandPhase Phase, int? ExitCode)[]
        {
            ("A", ShellCommandPhase.PromptStart, null),
            ("B", ShellCommandPhase.CommandStart, null),
            ("C", ShellCommandPhase.CommandExecuted, null),
            ("D;0", ShellCommandPhase.CommandDone, 0),
            ("D;1", ShellCommandPhase.CommandDone, 1),
            ("D", ShellCommandPhase.CommandDone, null),
        };

        StaTestRunner.Run(() =>
        {
            var view = new TerminalTabView("cmd.exe", Environment.CurrentDirectory);
            var events = new List<ShellCommandActivityEventArgs>();
            view.ShellCommandActivity += (_, e) => events.Add(e);

            const char esc = (char)0x1b;
            const char bel = (char)0x07;
            foreach (var (marker, _, _) in cases)
            {
                view.FeedOutputForTests($"{esc}]133;{marker}{bel}");
            }

            Assert.True(view.IsShellIntegrationActive);
            Assert.Equal(cases.Length, events.Count);
            for (int i = 0; i < cases.Length; i++)
            {
                Assert.Equal(cases[i].Phase, events[i].Phase);
                Assert.Equal(cases[i].ExitCode, events[i].ExitCode);
            }
        });
    }

    [Fact]
    public void ShellCommandActivityReportsCommandLineForEveryRunIncludingRepeats()
    {
        StaTestRunner.Run(() =>
        {
            var view = new TerminalTabView("cmd.exe", Environment.CurrentDirectory);
            var events = new List<ShellCommandActivityEventArgs>();
            var recorded = new List<string>();
            view.ShellCommandActivity += (_, e) => events.Add(e);
            view.CommandHistoryRecorded += (_, command) => recorded.Add(command);

            const char esc = (char)0x1b;
            const char bel = (char)0x07;
            string run = $"{esc}]133;A{bel}{esc}]133;B{bel}{esc}]633;E;dotnet build{bel}{esc}]133;C{bel}{esc}]133;D;1{bel}";
            view.FeedOutputForTests(run);
            view.FeedOutputForTests(run);
            view.FeedOutputForTests($"{esc}]133;A{bel}");

            var done = events.Where(e => e.Phase == ShellCommandPhase.CommandDone).ToList();
            Assert.Equal(2, done.Count);
            Assert.All(done, e => Assert.Equal("dotnet build", e.CommandLine));
            Assert.All(done, e => Assert.Equal(1, e.ExitCode));
            Assert.Equal(2, events.Count(e => e.Phase == ShellCommandPhase.CommandExecuted && e.CommandLine == "dotnet build"));
            Assert.All(events.Where(e => e.Phase is ShellCommandPhase.PromptStart or ShellCommandPhase.CommandStart),
                e => Assert.Null(e.CommandLine));
            Assert.Single(recorded);   // 履歴側は連続重複をまとめる
        });
    }

    [Fact]
    public void IsStickyScrollEnabledDefaultsTrueAndRoundTrips()
    {
        StaTestRunner.Run(() =>
        {
            var view = new TerminalTabView("cmd.exe", Environment.CurrentDirectory);

            Assert.True(view.IsStickyScrollEnabled);
            view.IsStickyScrollEnabled = false;
            Assert.False(view.IsStickyScrollEnabled);
            Assert.Null(view.StickyCommandLine);
        });
    }
}
