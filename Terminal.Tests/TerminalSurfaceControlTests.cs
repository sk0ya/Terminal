using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;

using Terminal.Buffer;
using Terminal.Rendering;

namespace Terminal.Tests;

public sealed class TerminalSurfaceControlTests
{
    [Fact]
    public void SurfaceCountsMatchesAndSelectsForward()
    {
        RunSta(() =>
        {
            var surface = CreateSurface();
            surface.UpdateSnapshot(new AnsiTerminalBuffer.TerminalRenderSnapshot(
            [
                CreateLine("alpha beta"),
                CreateLine("beta gamma")
            ]));

            Assert.Equal(2, surface.CountMatches("beta", StringComparison.Ordinal));

            Assert.True(surface.TrySelectNextMatch("beta", StringComparison.Ordinal, forward: true, out bool wrapped));
            Assert.False(wrapped);
            Assert.Equal("beta", surface.GetSelectedText());

            Assert.True(surface.TrySelectNextMatch("beta", StringComparison.Ordinal, forward: true, out wrapped));
            Assert.False(wrapped);
            Assert.Equal("beta", surface.GetSelectedText());
        });
    }

    [Fact]
    public void SurfaceWrapsSearchBackward()
    {
        RunSta(() =>
        {
            var surface = CreateSurface();
            surface.UpdateSnapshot(new AnsiTerminalBuffer.TerminalRenderSnapshot(
            [
                CreateLine("first"),
                CreateLine("second"),
                CreateLine("first")
            ]));

            Assert.True(surface.TrySelectNextMatch("first", StringComparison.Ordinal, forward: true, out bool wrapped));
            Assert.False(wrapped);
            Assert.Equal("first", surface.GetSelectedText());

            Assert.True(surface.TrySelectNextMatch("first", StringComparison.Ordinal, forward: false, out wrapped));
            Assert.True(wrapped);
            Assert.Equal("first", surface.GetSelectedText());
        });
    }

    [Fact]
    public void SurfaceExposesStableCellGeometry()
    {
        RunSta(() =>
        {
            var surface = CreateSurface();
            surface.UpdateSnapshot(new AnsiTerminalBuffer.TerminalRenderSnapshot(
            [
                CreateLine("line-1"),
                CreateLine("line-2")
            ]));

            Size cell = surface.CharacterCellSize;
            Rect first = surface.GetCellRect(0, 0);
            Rect secondLine = surface.GetCellRect(1, 0);
            Rect thirdColumn = surface.GetCellRect(0, 3);

            Assert.Equal(cell.Width, first.Width);
            Assert.Equal(cell.Height, first.Height);
            Assert.Equal(first.Top + cell.Height, secondLine.Top, precision: 3);
            Assert.Equal(first.Left + (cell.Width * 3), thirdColumn.Left, precision: 3);
        });
    }

    [Fact]
    public void SurfaceCellPositionsTrackRenderedTextWidth()
    {
        RunSta(() =>
        {
            var surface = CreateSurface();
            const string sample = "PS C:\\Projects\\Terminal> ";

            surface.UpdateSnapshot(new AnsiTerminalBuffer.TerminalRenderSnapshot(
            [
                CreateLine(sample)
            ]));

            Rect cursorCell = surface.GetCellRect(0, sample.Length);
            double renderedWidth = MeasureTextWidth(surface, sample);

            Assert.Equal(renderedWidth, cursorCell.Left, precision: 1);
        });
    }

    private static TerminalSurfaceControl CreateSurface()
    {
        var surface = new TerminalSurfaceControl
        {
            FontFamily = new FontFamily("Cascadia Mono"),
            FontSize = 14,
            Width = 640,
            Height = 320
        };

        surface.Measure(new Size(640, 320));
        surface.Arrange(new Rect(0, 0, 640, 320));
        surface.UpdateLayout();
        return surface;
    }

    private static AnsiTerminalBuffer.TerminalRenderLineSnapshot CreateLine(string text)
    {
        return new AnsiTerminalBuffer.TerminalRenderLineSnapshot(
            AnchorSegmentIndex: -1,
            CellLength: text.Length,
            [
                new AnsiTerminalBuffer.TerminalRenderSegmentSnapshot(
                    text,
                    CellLength: text.Length,
                    Colors.White,
                    Colors.Black,
                    Bold: false,
                    Underline: false,
                    Hyperlink: null)
            ]);
    }

    private static double MeasureTextWidth(TerminalSurfaceControl surface, string text)
    {
        var typeface = new Typeface(
            surface.FontFamily,
            surface.FontStyle,
            surface.FontWeight,
            surface.FontStretch);
        var formatted = new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            surface.FontSize,
            Brushes.White,
            VisualTreeHelper.GetDpi(surface).PixelsPerDip);
        return formatted.WidthIncludingTrailingWhitespace;
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
