using Terminal.Tabs;
using Xunit;

namespace Terminal.Tests;

/// <summary>
/// The raw application title (OSC 0/2) is exposed alongside the shaped tab header, because the header
/// drops path-like segments: an agent status title such as "◐ Fix src/foo.cs" would otherwise only
/// ever reach the host as the profile name.
/// </summary>
public sealed class TerminalWindowTitleTests
{
    [Fact]
    public void WindowTitleChangedCarriesTheRawTitleEvenWhenTheHeaderDropsIt()
    {
        StaTestRunner.Run(() =>
        {
            var view = new TerminalTabView("cmd.exe", Environment.CurrentDirectory);
            var raw = new List<string>();
            view.WindowTitleChanged += (_, title) => raw.Add(title);

            const char esc = (char)0x1b;
            const char bel = (char)0x07;
            view.FeedOutputForTests($"{esc}]0;◐ Fix src/foo.cs{bel}");
            view.RefreshTitleForTests();
            view.RefreshTitleForTests();   // unchanged: no second event
            view.FeedOutputForTests($"{esc}]0;✳ Fix src/foo.cs{bel}");
            view.RefreshTitleForTests();

            Assert.Equal(["◐ Fix src/foo.cs", "✳ Fix src/foo.cs"], raw);
            Assert.Equal("✳ Fix src/foo.cs", view.WindowTitle);
            Assert.DoesNotContain("src/foo.cs", view.HeaderTitle);
        });
    }
}
