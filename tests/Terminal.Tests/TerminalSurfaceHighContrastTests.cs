using System.Windows.Media;
using Terminal.Rendering;

namespace Terminal.Tests;

public sealed class TerminalSurfaceHighContrastTests
{
    [Fact]
    public void HighContrastOverridesExplicitTrueColorForegroundAndBackground()
    {
        StaTestRunner.Run(() =>
        {
            var surface = new TerminalSurfaceControl
            {
                Foreground = new SolidColorBrush(Colors.Yellow),
                Background = new SolidColorBrush(Colors.Black),
                HighContrastMode = true
            };

            Assert.Equal(Colors.Yellow, surface.ResolveForegroundColor(Colors.DarkRed));
            Assert.Equal(Colors.Black, surface.ResolveBackgroundColor(Colors.DarkBlue));
            surface.HighContrastMode = false;
            Assert.Equal(Colors.DarkRed, surface.ResolveForegroundColor(Colors.DarkRed));
            Assert.Equal(Colors.DarkBlue, surface.ResolveBackgroundColor(Colors.DarkBlue));
        });
    }
}
