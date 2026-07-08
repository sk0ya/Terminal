using System.Linq;
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
    public void FindMatchesDoesNotChangeTheCurrentSelection()
    {
        RunSta(() =>
        {
            var surface = CreateSurface();
            surface.UpdateSnapshot(new AnsiTerminalBuffer.TerminalRenderSnapshot(
            [
                CreateLine("alpha beta"),
                CreateLine("beta gamma")
            ]));
            Assert.True(surface.SelectMatch(0, 0, 5));

            var matches = surface.FindMatches("beta", StringComparison.Ordinal);

            Assert.Equal(2, matches.Count);
            Assert.Equal("alpha", surface.GetSelectedText());
        });
    }

    [Fact]
    public void SelectMatchClampsColumnsAndRejectsInvalidLines()
    {
        RunSta(() =>
        {
            var surface = CreateSurface();
            surface.UpdateSnapshot(new AnsiTerminalBuffer.TerminalRenderSnapshot(
            [
                CreateLine("alpha")
            ]));

            Assert.True(surface.SelectMatch(0, -10, 99));
            Assert.Equal("alpha", surface.GetSelectedText());
            Assert.False(surface.SelectMatch(1, 0, 1));
            Assert.Equal("alpha", surface.GetSelectedText());
        });
    }

    [Fact]
    public void KeyboardSelectionCrossesLineBoundariesAndCanBeCleared()
    {
        RunSta(() =>
        {
            var surface = CreateSurface();
            surface.UpdateSnapshot(new AnsiTerminalBuffer.TerminalRenderSnapshot(
            [
                CreateLine("ab"),
                CreateLine("cd")
            ]));

            Assert.True(surface.MoveKeyboardCursor(System.Windows.Input.Key.Right, extend: true));
            Assert.True(surface.MoveKeyboardCursor(System.Windows.Input.Key.Right, extend: true));
            Assert.True(surface.MoveKeyboardCursor(System.Windows.Input.Key.Right, extend: true));

            Assert.True(surface.HasSelection);
            Assert.Equal("ab" + Environment.NewLine, surface.GetSelectedText());

            surface.ClearSelection();

            Assert.False(surface.HasSelection);
            Assert.Equal(string.Empty, surface.GetSelectedText());
        });
    }

    [Fact]
    public void SurfaceBuildsLineLayoutsLazilyAndReachesOffscreenLines()
    {
        RunSta(() =>
        {
            var surface = CreateSurface();
            const int lineCount = 10_000;
            AnsiTerminalBuffer.TerminalRenderLineSnapshot[] lines = Enumerable
                .Range(0, lineCount)
                .Select(index => CreateLine($"line-{index:D5} content"))
                .ToArray();

            surface.UpdateSnapshot(new AnsiTerminalBuffer.TerminalRenderSnapshot(lines));

            // Adopting a snapshot does not materialize the whole history; layouts are built on
            // demand, so only a handful (from cursor/selection coercion) exist up front.
            Assert.Equal(lineCount, surface.LineCount);
            Assert.True(
                surface.CachedLineLayoutCount < 50,
                $"Expected lazy layout construction but {surface.CachedLineLayoutCount} of {lineCount} lines were built up front.");

            // Search and selection reach a line far outside the viewport, proving off-screen layouts
            // are produced on demand from the lightweight value snapshot.
            Assert.Equal(1, surface.CountMatches("line-09999", StringComparison.Ordinal));
            Assert.True(surface.TrySelectNextMatch("line-09999", StringComparison.Ordinal, forward: true, out bool wrapped));
            Assert.False(wrapped);
            Assert.Equal("line-09999", surface.GetSelectedText());
        });
    }

    [Fact]
    public void SurfaceReusesUnchangedLayoutsAndEvictsChangedOrRemovedLines()
    {
        RunSta(() =>
        {
            var surface = CreateSurface();
            surface.UpdateSnapshot(new AnsiTerminalBuffer.TerminalRenderSnapshot(
            [
                CreateLine("stable top"),
                CreateLine("stable middle"),
                CreateLine("changing bottom 1"),
                CreateLine("trailing a"),
                CreateLine("trailing b")
            ]));

            // Force every layout to be materialized.
            surface.CountMatches("stable", StringComparison.Ordinal);
            Assert.Equal(5, surface.CachedLineLayoutCount);

            // The next snapshot keeps the two stable top lines, changes the third, and drops the
            // trailing two. Only the unchanged lines survive in the cache.
            surface.UpdateSnapshot(new AnsiTerminalBuffer.TerminalRenderSnapshot(
            [
                CreateLine("stable top"),
                CreateLine("stable middle"),
                CreateLine("changing bottom 2")
            ]));

            Assert.Equal(2, surface.CachedLineLayoutCount);
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

            surface.SelectRange(
                new TerminalTextRange(new(0, 0), new(2, 6)),
                blockSelection: true,
                blockAnchorCellColumn: 1,
                blockCurrentCellColumn: 4);

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

            surface.SelectRange(
                new TerminalTextRange(new(0, 0), new(1, 6)),
                blockSelection: true,
                blockAnchorCellColumn: 1,
                blockCurrentCellColumn: 3);

            // Verify block selected text is non-empty before clear
            Assert.False(string.IsNullOrEmpty(surface.GetSelectedText()));

            // ClearSelection should reset _blockSelectionMode
            surface.ClearSelection();

            // After clearing, GetSelectedText should return empty (no selection)
            Assert.Equal(string.Empty, surface.GetSelectedText());
            Assert.False(surface.HasSelection);

        });
    }

    [Fact]
    public void Surface_CachesLineDrawablesAcrossRepaints()
    {
        RunSta(() =>
        {
            var surface = CreateSurface();
            surface.UpdateSnapshot(new AnsiTerminalBuffer.TerminalRenderSnapshot(
            [
                CreateLine("first line"),
                CreateLine("second line")
            ]));

            Assert.Equal(0, surface.CachedLineDrawableCount);

            ForceRender(surface);
            int afterFirstRender = surface.CachedLineDrawableCount;
            Assert.True(afterFirstRender > 0, "Expected visible lines to shape and cache their drawables.");

            // A second paint with no content change reuses the cached drawables (count is unchanged,
            // not rebuilt from scratch).
            ForceRender(surface);
            Assert.Equal(afterFirstRender, surface.CachedLineDrawableCount);
        });
    }

    [Fact]
    public void Surface_TogglingLigatures_InvalidatesAndRebuildsDrawables()
    {
        RunSta(() =>
        {
            var surface = CreateSurface();
            surface.UpdateSnapshot(new AnsiTerminalBuffer.TerminalRenderSnapshot(
            [
                CreateLine("a => b != c -> d")
            ]));

            ForceRender(surface);
            Assert.True(surface.CachedLineDrawableCount > 0);

            // Flipping the ligature mode must drop the stale shaped drawables so the next paint
            // re-shapes through the ligature-aware path.
            surface.FontLigaturesEnabled = true;
            Assert.Equal(0, surface.CachedLineDrawableCount);

            ForceRender(surface);
            Assert.True(surface.CachedLineDrawableCount > 0);
        });
    }

    [Fact]
    public void Surface_ChangingFontMetrics_InvalidatesDrawables()
    {
        RunSta(() =>
        {
            var surface = CreateSurface();
            surface.UpdateSnapshot(new AnsiTerminalBuffer.TerminalRenderSnapshot(
            [
                CreateLine("metrics line")
            ]));

            ForceRender(surface);
            Assert.True(surface.CachedLineDrawableCount > 0);

            // A font-size change reshapes every glyph, so the cached drawables must be discarded.
            surface.FontSize = 18;
            Assert.Equal(0, surface.CachedLineDrawableCount);
        });
    }

    [Fact]
    public void SurfaceScrollAdapterClampsOffsetsWhenSnapshotShrinks()
    {
        RunSta(() =>
        {
            var surface = CreateSurface();
            surface.UpdateSnapshot(new AnsiTerminalBuffer.TerminalRenderSnapshot(
                Enumerable.Range(0, 100).Select(index => CreateLine(new string('x', 200) + index)).ToArray()));
            surface.SetHorizontalOffset(double.PositiveInfinity);
            surface.SetVerticalOffset(double.PositiveInfinity);
            Assert.True(surface.HorizontalOffset > 0);
            Assert.True(surface.VerticalOffset > 0);

            surface.UpdateSnapshot(new AnsiTerminalBuffer.TerminalRenderSnapshot([CreateLine("short")]));

            Assert.Equal(0, surface.HorizontalOffset);
            Assert.Equal(0, surface.VerticalOffset);
        });
    }

    [Fact]
    public void SurfaceMakeVisibleUsesScrollStateOffsetsAndReturnsIntersection()
    {
        RunSta(() =>
        {
            var surface = CreateSurface();
            surface.UpdateSnapshot(new AnsiTerminalBuffer.TerminalRenderSnapshot(
                Enumerable.Range(0, 100).Select(_ => CreateLine(new string('x', 200))).ToArray()));
            var target = new Rect(700, 500, 40, 20);

            Rect visible = surface.MakeVisible(surface, target);

            Assert.Equal(target, visible);
            Assert.Equal(target.Right - surface.ViewportWidth, surface.HorizontalOffset, precision: 3);
            Assert.Equal(target.Bottom - surface.ViewportHeight, surface.VerticalOffset, precision: 3);
        });
    }

    [Fact]
    public void Surface_Unloaded_ReleasesCachedDrawablesAndCanRenderAfterReload()
    {
        RunSta(() =>
        {
            var surface = CreateSurface();
            surface.FontLigaturesEnabled = true;
            surface.UpdateSnapshot(new AnsiTerminalBuffer.TerminalRenderSnapshot(
            [
                CreateLine("reload line")
            ]));
            ForceRender(surface);
            Assert.True(surface.CachedLineDrawableCount > 0);
            Assert.True(surface.HasTextFormatter);

            surface.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));

            Assert.Equal(0, surface.CachedLineDrawableCount);
            Assert.Equal(0, surface.CachedLineLayoutCount);
            Assert.Equal(1, surface.LineCount);
            Assert.False(surface.HasTextFormatter);
            ForceRender(surface);
            Assert.True(surface.CachedLineDrawableCount > 0);
            Assert.True(surface.HasTextFormatter);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Surface_RendersMixedContentWithoutThrowing(bool ligaturesEnabled)
    {
        RunSta(() =>
        {
            var surface = CreateSurface();
            surface.FontLigaturesEnabled = ligaturesEnabled;
            surface.UpdateSnapshot(new AnsiTerminalBuffer.TerminalRenderSnapshot(
            [
                CreateLine("=> != -> >= <= == === |> <|"),
                CreateLine("ascii 日本語 mix 絵文字😀 tail"),
                CreateDecoratedLine("underlined strikethrough")
            ]));

            // Exercises both the ligature TextFormatter path (primary-font runs) and the
            // FormattedText fallback path (wide/CJK/emoji runs and decorated runs) in one paint.
            ForceRender(surface);

            Assert.True(surface.CachedLineDrawableCount > 0);
        });
    }

    private static void ForceRender(TerminalSurfaceControl surface)
    {
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var onRender = typeof(TerminalSurfaceControl).GetMethod("OnRender", flags, [typeof(DrawingContext)])!;
        var visual = new DrawingVisual();
        using DrawingContext context = visual.RenderOpen();
        onRender.Invoke(surface, [context]);
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

    private static AnsiTerminalBuffer.TerminalRenderLineSnapshot CreateDecoratedLine(string text)
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
                    Bold: true,
                    Italic: true,
                    UnderlineStyle: UnderlineStyle.Curly,
                    UnderlineColor: Colors.Red,
                    Strikethrough: true,
                    Overline: true,
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
