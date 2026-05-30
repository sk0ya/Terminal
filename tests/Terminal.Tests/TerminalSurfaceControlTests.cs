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

    [Fact]
    public void SurfaceSnapsLineHeightToDevicePixelsWithoutShrinkingGlyphBounds()
    {
        RunSta(() =>
        {
            var surface = CreateSurface();

            Size cell = surface.CharacterCellSize;
            DpiScale dpi = VisualTreeHelper.GetDpi(surface);
            double devicePixelHeight = cell.Height * dpi.DpiScaleY;
            double measuredTextHeight = MeasureTextHeight(surface, "W");

            Assert.Equal(Math.Round(devicePixelHeight), devicePixelHeight, precision: 6);
            Assert.True(cell.Height >= measuredTextHeight);
        });
    }

    [Fact]
    public void SurfaceKeepsExtentAtLeastViewportFloorWhenSnapshotShrinks()
    {
        RunSta(() =>
        {
            var surface = CreateSurface();

            surface.SetViewportFloor(new Size(640, 320));
            surface.UpdateSnapshot(new AnsiTerminalBuffer.TerminalRenderSnapshot(
            [
                CreateLine("short")
            ]));

            Assert.Equal(640, surface.ExtentWidth, precision: 3);
            Assert.Equal(320, surface.ExtentHeight, precision: 3);

            surface.UpdateSnapshot(new AnsiTerminalBuffer.TerminalRenderSnapshot(
            [
                CreateLine(string.Empty)
            ]));
            surface.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            Assert.Equal(640, surface.ExtentWidth, precision: 3);
            Assert.Equal(320, surface.ExtentHeight, precision: 3);
            Assert.Equal(640, surface.DesiredSize.Width, precision: 3);
            Assert.Equal(320, surface.DesiredSize.Height, precision: 3);
        });
    }

    [Fact]
    public void BlockSelection_GetSelectedText_ExtractsCorrectColumns()
    {
        RunSta(() =>
        {
            var surface = CreateSurface();
            surface.UpdateSnapshot(new AnsiTerminalBuffer.TerminalRenderSnapshot(
            [
                CreateLine("abcdef"),
                CreateLine("ABCDEF"),
                CreateLine("012345")
            ]));

            // Simulate a block selection of columns 1–3 across all three lines
            // by using internal helpers via TrySelectNextMatch as a workaround.
            // Instead, we call the public API: use reflection to set private state
            // that would result from an Alt+drag from column 1 to column 4.
            var type = surface.GetType();
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

            // Set _blockSelectionMode = true
            type.GetField("_blockSelectionMode", flags)!.SetValue(surface, true);
            // Set _blockAnchorCellColumn = 1.0
            type.GetField("_blockAnchorCellColumn", flags)!.SetValue(surface, 1.0);
            // Set _blockCurrentCellColumn = 4.0
            type.GetField("_blockCurrentCellColumn", flags)!.SetValue(surface, 4.0);
            // Set _selection to span all three lines
            var posType = type.GetNestedType("TerminalTextPosition", flags)!;
            var rangeType = type.GetNestedType("TerminalTextRange", flags)!;
            var start = System.Activator.CreateInstance(posType, 0, 0)!;
            var end = System.Activator.CreateInstance(posType, 2, 6)!;
            var range = System.Activator.CreateInstance(rangeType, start, end)!;
            type.GetField("_selection", flags)!.SetValue(surface, range);

            string selected = surface.GetSelectedText();
            string[] lines = selected.Split(["\r\n", "\n"], StringSplitOptions.None);

            // columns 1–4 (leftColumn=1, rightColumn=4) → "bcd", "BCD", "123"
            Assert.Equal(3, lines.Length);
            Assert.Equal("bcd", lines[0]);
            Assert.Equal("BCD", lines[1]);
            Assert.Equal("123", lines[2]);
        });
    }

    [Fact]
    public void BlockSelection_ClearSelection_ResetsBlockMode()
    {
        RunSta(() =>
        {
            var surface = CreateSurface();
            surface.UpdateSnapshot(new AnsiTerminalBuffer.TerminalRenderSnapshot(
            [
                CreateLine("abcdef"),
                CreateLine("ABCDEF")
            ]));

            var type = surface.GetType();
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

            // Simulate block selection state
            type.GetField("_blockSelectionMode", flags)!.SetValue(surface, true);
            type.GetField("_blockAnchorCellColumn", flags)!.SetValue(surface, 1.0);
            type.GetField("_blockCurrentCellColumn", flags)!.SetValue(surface, 3.0);
            var posType = type.GetNestedType("TerminalTextPosition", flags)!;
            var rangeType = type.GetNestedType("TerminalTextRange", flags)!;
            var start = System.Activator.CreateInstance(posType, 0, 0)!;
            var end = System.Activator.CreateInstance(posType, 1, 6)!;
            var range = System.Activator.CreateInstance(rangeType, start, end)!;
            type.GetField("_selection", flags)!.SetValue(surface, range);

            // Verify block selected text is non-empty before clear
            Assert.False(string.IsNullOrEmpty(surface.GetSelectedText()));

            // ClearSelection should reset _blockSelectionMode
            surface.ClearSelection();

            // After clearing, GetSelectedText should return empty (no selection)
            Assert.Equal(string.Empty, surface.GetSelectedText());
            Assert.False(surface.HasSelection);

            // Also verify the private field was reset
            bool blockMode = (bool)type.GetField("_blockSelectionMode", flags)!.GetValue(surface)!;
            Assert.False(blockMode);
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
                    Italic: false,
                    UnderlineStyle: UnderlineStyle.None,
                    UnderlineColor: null,
                    Strikethrough: false,
                    Overline: false,
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

    private static double MeasureTextHeight(TerminalSurfaceControl surface, string text)
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
        return formatted.Height;
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
