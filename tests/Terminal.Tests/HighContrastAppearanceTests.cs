using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Terminal.Tests;

public sealed class HighContrastAppearanceTests
{
    [Fact]
    public void MainWindowChromeResourcesSwitchAndRestore()
    {
        StaTestRunner.Run(() =>
        {
            var window = new MainWindow();
            object original = window.Resources["ChromeBrush"];
            Brush originalForeground = window.Foreground;
            typeof(MainWindow).GetMethod("ApplyChromeTheme", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(window, [true]);
            Assert.Equal(SystemColors.WindowBrush, window.Resources["ChromeBrush"]);
            Assert.Equal(SystemColors.WindowTextBrush, window.Resources["TabTextBrush"]);
            Assert.Equal(SystemColors.HighlightBrush, window.Resources["CommandHoverBrush"]);
            Assert.Equal(SystemColors.HotTrackBrush, window.Resources["ClosePressedBrush"]);
            Assert.Equal(SystemColors.HighlightTextBrush, window.Resources["StateTextBrush"]);
            typeof(MainWindow).GetField("_highContrastActive", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(window, true);
            typeof(MainWindow).GetMethod("ApplyBackdrop", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(window, [new Terminal.Settings.TerminalAppSettings { BackdropType = "none" }]);
            Assert.Equal(SystemColors.WindowTextBrush, window.Foreground);
            typeof(MainWindow).GetMethod("ApplyChromeTheme", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(window, [false]);
            typeof(MainWindow).GetField("_highContrastActive", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(window, false);
            typeof(MainWindow).GetMethod("ApplyBackdrop", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(window, [new Terminal.Settings.TerminalAppSettings { BackdropType = "none" }]);
            Assert.Same(original, window.Resources["ChromeBrush"]);
            Assert.Same(originalForeground, window.Foreground);
        });
    }

    [Fact]
    public void ReapplyThemesContentAddedAfterDialogWasCreated()
    {
        StaTestRunner.Run(() =>
        {
            var panel = new StackPanel();
            var window = new Window { Content = panel };
            HighContrastAppearance.Apply(window, true);
            var later = new TextBlock { Foreground = Brushes.Coral };
            panel.Children.Add(later);
            HighContrastAppearance.Apply(window, true);
            Assert.Equal(SystemColors.WindowTextBrush, later.Foreground);
            HighContrastAppearance.Apply(window, false);
            Assert.Equal(Brushes.Coral, later.Foreground);
        });
    }

    [Fact]
    public void ApplyRestoresDialogLocalColorsWhenHighContrastTurnsOff()
    {
        StaTestRunner.Run(() =>
        {
            var text = new TextBlock { Foreground = Brushes.Coral };
            var panel = new Border { Background = Brushes.Navy, Child = text };
            var window = new Window { Background = Brushes.Purple, Foreground = Brushes.Lime, Content = panel };

            HighContrastAppearance.Apply(window, true);
            Assert.Equal(SystemColors.WindowBrush, window.Background);
            Assert.Equal(SystemColors.WindowTextBrush, text.Foreground);

            HighContrastAppearance.Apply(window, false);
            Assert.Equal(Brushes.Purple, window.Background);
            Assert.Equal(Brushes.Lime, window.Foreground);
            Assert.Equal(Brushes.Navy, panel.Background);
            Assert.Equal(Brushes.Coral, text.Foreground);
        });
    }
}
