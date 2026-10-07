using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

using Terminal.Tabs;

namespace Terminal.Tests;

/// <summary>Esc/F3 reach the terminal while the find panel is open but not focused.</summary>
public sealed class TerminalFindPanelKeyTests
{
    [Theory]
    [InlineData(Key.Escape)]
    [InlineData(Key.F3)]
    public void KeysAreNotTakenWhenTheFindPanelIsOpenButUnfocused(Key key)
    {
        StaTestRunner.Run(() =>
        {
            var view = new TerminalTabView("cmd.exe", Environment.CurrentDirectory);
            var window = new Window { Content = view, Width = 400, Height = 300, ShowActivated = false, WindowStyle = WindowStyle.None };
            window.Show();
            try
            {
                view.FindPopup.IsOpen = true;
                Assert.False(view.IsFindPanelFocused());

                var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(view)!, 0, key)
                {
                    RoutedEvent = Keyboard.PreviewKeyDownEvent
                };
                view.RaiseEvent(args);

                Assert.False(args.Handled);
                Assert.True(view.FindPopup.IsOpen);
            }
            finally
            {
                view.FindPopup.IsOpen = false;
                window.Close();
            }
        });
    }
}
