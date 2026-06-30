using System.Windows.Media;

using Terminal.Settings;
using Terminal.Tabs;

namespace Terminal.Tests;

public sealed class HistoryPopupThemeTests
{
    [Fact]
    public void SetColorThemeRecolorsHistoryPopupResources()
    {
        StaTestRunner.Run(() =>
        {
            var view = new TerminalTabView("cmd.exe", Environment.CurrentDirectory);

            Color selection = Color.FromArgb(0x66, 0x20, 0xC0, 0x40);
            var theme = new TerminalColorTheme(
                foreground: Colors.White,
                background: Colors.Black,
                ansiPalette: TerminalColorTheme.Default.AnsiPalette,
                cursor: Colors.Magenta,
                selectionBackground: selection);

            view.SetColorTheme(theme);

            // Accent (prompt / pointer / match highlight / caret) is the selection
            // hue brightened 55% toward the foreground, so matched characters stay
            // visible on the auto-selected row (whose background is the selection colour).
            Assert.Equal(
                Color.FromRgb(0x9B, 0xE3, 0xA9),
                ((SolidColorBrush)view.Resources["HistoryPopupAccentBrush"]).Color);

            // Selected-row background keeps the selection colour including its alpha.
            Assert.Equal(selection, ((SolidColorBrush)view.Resources["HistoryPopupSelectionBrush"]).Color);

            // Foreground tracks the theme foreground.
            Assert.Equal(Colors.White, ((SolidColorBrush)view.Resources["HistoryPopupForegroundBrush"]).Color);
        });
    }
}
