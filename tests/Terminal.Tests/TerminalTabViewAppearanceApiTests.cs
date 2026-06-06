using System.Runtime.ExceptionServices;
using System.Reflection;
using System.Threading;
using System.Windows.Media;

using Terminal.Buffer;
using Terminal.Settings;
using Terminal.Tabs;

namespace Terminal.Tests;

public sealed class TerminalTabViewAppearanceApiTests
{
    [Fact]
    public void SetFontFamilyAndSizeExposeEffectiveValues()
    {
        RunSta(() =>
        {
            var view = new TerminalTabView("cmd.exe", Environment.CurrentDirectory);

            view.SetFontFamily("consolas");
            view.SetFontSize(100);

            Assert.Equal("Consolas", view.FontFamilyName);
            Assert.Equal(24, view.TerminalFontSize);
        });
    }

    [Fact]
    public void SetColorThemeExposesEffectiveTheme()
    {
        RunSta(() =>
        {
            var theme = new TerminalColorTheme(
                Colors.LightGray,
                Colors.DarkBlue,
                TerminalColorTheme.Default.AnsiPalette,
                Colors.Yellow,
                Color.FromArgb(0x55, 0x20, 0x40, 0x60));
            var view = new TerminalTabView("cmd.exe", Environment.CurrentDirectory);

            view.SetColorTheme(theme);

            Assert.Same(theme, view.ColorTheme);
        });
    }

    [Fact]
    public void ReplacingTerminalBufferPreservesColorTheme()
    {
        RunSta(() =>
        {
            var theme = new TerminalColorTheme(
                Colors.LightGray,
                Colors.DarkBlue,
                TerminalColorTheme.Default.AnsiPalette,
                Colors.Yellow);
            var view = new TerminalTabView("cmd.exe", Environment.CurrentDirectory);
            view.SetColorTheme(theme);

            var nextBuffer = new AnsiTerminalBuffer(80, 24);
            MethodInfo replaceTerminalBuffer = typeof(TerminalTabView).GetMethod(
                "ReplaceTerminalBuffer",
                BindingFlags.Instance | BindingFlags.NonPublic)!;

            replaceTerminalBuffer.Invoke(view, [nextBuffer]);

            Assert.Same(theme, nextBuffer.ColorTheme);
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
