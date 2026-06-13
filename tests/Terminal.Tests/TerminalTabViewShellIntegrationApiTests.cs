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

}
