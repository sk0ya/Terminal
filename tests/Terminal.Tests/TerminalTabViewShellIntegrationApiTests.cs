using System.Runtime.ExceptionServices;
using System.Threading;

using Terminal.Tabs;

namespace Terminal.Tests;

public sealed class TerminalTabViewShellIntegrationApiTests
{
    [Fact]
    public void ShellIntegrationInjectionEnabledRoundTrips()
    {
        RunSta(() =>
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
        RunSta(() =>
        {
            var view = new TerminalTabView("cmd.exe", Environment.CurrentDirectory);

            view.IsStatusBarVisible = true;
            Assert.True(view.IsStatusBarVisible);

            view.IsStatusBarVisible = false;
            Assert.False(view.IsStatusBarVisible);
        });
    }

    [Fact]
    public void IsShellIntegrationActiveIsFalseBeforeAnyMarkerArrives()
    {
        RunSta(() =>
        {
            var view = new TerminalTabView("cmd.exe", Environment.CurrentDirectory);

            Assert.False(view.IsShellIntegrationActive);
        });
    }

    private static void RunSta(Action action)
    {
        ExceptionDispatchInfo? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                captured = ExceptionDispatchInfo.Capture(ex);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        captured?.Throw();
    }
}
