using System.Windows;
using System.Windows.Controls;

namespace Terminal.Tests;

public sealed class DetachedTerminalWindowTests
{
    [Fact]
    public void DetachedWindowHostsTheExistingContentAndUsesTerminalTitle()
    {
        StaTestRunner.Run(() =>
        {
            var owner = new Window { Width = 800, Height = 600 };
            var content = new Border();

            var window = new DetachedTerminalWindow(content, [], "PowerShell", owner);

            Assert.Same(content, window.Content);
            Assert.Null(window.Owner);
            Assert.Equal("PowerShell - ConPTY Terminal", window.Title);
            Assert.Equal(900, window.Width);
            Assert.Equal(600, window.Height);

            window.Close();
        });
    }
}
