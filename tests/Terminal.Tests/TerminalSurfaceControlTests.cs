using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

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
    public void SurfaceRendersInlineImagePixels()
    {
        RunSta(() =>
        {
            var surface = CreateSurface();
            var image = new TerminalImage(
                [255, 255, 255, 255],
                TerminalImageDataKind.Bgra32,
                "image/bgra32",
                1,
                1,
                0,
                4,
                3,
                null,
                null,
                true);
            surface.UpdateSnapshot(new AnsiTerminalBuffer.TerminalRenderSnapshot(
            [
                new AnsiTerminalBuffer.TerminalRenderLineSnapshot(
                    CellLength: 0,
                    Segments: [],
                    Images: [image])
            ]));

            var visual = new DrawingVisual();
            using (DrawingContext context = visual.RenderOpen())
            {
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                var onRender = typeof(TerminalSurfaceControl).GetMethod("OnRender", flags, [typeof(DrawingContext)])!;
                onRender.Invoke(surface, [context]);
            }

            var bitmap = new RenderTargetBitmap(640, 320, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            byte[] pixels = new byte[640 * 320 * 4];
            bitmap.CopyPixels(pixels, 640 * 4, 0);

            Assert.Contains(
                pixels.Chunk(4),
                pixel => pixel[0] >= 240 && pixel[1] >= 240 && pixel[2] >= 240 && pixel[3] >= 240);
        });
    }

    [Fact]
    public void SurfaceReusesDecodedBitmapWhenReflowMovesAnImage()
    {
        RunSta(() =>
        {
            var surface = CreateSurface();
            var image = new TerminalImage(
                [0, 0, 255, 255],
                TerminalImageDataKind.Bgra32,
                "image/bgra32",
                1,
                1,
                0,
                4,
                3,
                null,
                null,
                true);

            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var onRender = typeof(TerminalSurfaceControl).GetMethod("OnRender", flags, [typeof(DrawingContext)])!;
            var cacheField = typeof(TerminalSurfaceControl).GetField("_imageCache", flags)!;

            void RenderWith(TerminalImage placed)
            {
                surface.UpdateSnapshot(new AnsiTerminalBuffer.TerminalRenderSnapshot(
                [
                    new AnsiTerminalBuffer.TerminalRenderLineSnapshot(
                        CellLength: 0,
                        Segments: [],
                        Images: [placed])
                ]));
                var visual = new DrawingVisual();
                using DrawingContext context = visual.RenderOpen();
                onRender.Invoke(surface, [context]);
            }

            RenderWith(image);
            // A reflow rebuilds the record with a new anchor column but keeps the same payload.
            RenderWith(image with { Column = 5 });

            int entries = ((System.Collections.ICollection)cacheField.GetValue(surface)!).Count;
            Assert.Equal(1, entries);
        });
    }
    [Fact]
    public void SurfaceKeepsMultiRowImageUnderTheRowsBelowIt()
    {
        RunSta(() =>
        {
            var surface = CreateSurface();
            // Three cells tall, anchored on row 0, with ordinary text on the rows it covers. Cell
            // backgrounds are opaque, so painting them line by line used to erase the image body.
            var image = new TerminalImage(
                [0, 0, 255, 255],
                TerminalImageDataKind.Bgra32,
                "image/bgra32",
                1,
                1,
                0,
                4,
                3,
                null,
                null,
                true);
            surface.UpdateSnapshot(new AnsiTerminalBuffer.TerminalRenderSnapshot(
            [
                new AnsiTerminalBuffer.TerminalRenderLineSnapshot(
                    CellLength: 0,
                    Segments: [],
                    Images: [image]),
                CreateLine("below one"),
                CreateLine("below two")
            ]));

            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            double cellHeight = ((Size)typeof(TerminalSurfaceControl)
                .GetField("_cellSize", flags)!
                .GetValue(surface)!).Height;
            Assert.True(cellHeight > 0);

            var visual = new DrawingVisual();
            using (DrawingContext context = visual.RenderOpen())
            {
                var onRender = typeof(TerminalSurfaceControl).GetMethod("OnRender", flags, [typeof(DrawingContext)])!;
                onRender.Invoke(surface, [context]);
            }

            var bitmap = new RenderTargetBitmap(640, 320, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            byte[] pixels = new byte[640 * 320 * 4];
            bitmap.CopyPixels(pixels, 640 * 4, 0);

            int lowestImageRow = -1;
            for (int y = 0; y < 320; y++)
            {
                for (int x = 0; x < 640; x++)
                {
                    int offset = ((y * 640) + x) * 4;
                    if (pixels[offset + 2] >= 200 && pixels[offset + 1] < 80 && pixels[offset] < 80)
                    {
                        lowestImageRow = y;
                        break;
                    }
                }
            }

            // The image reaches into the third text row, well past the first one.
            Assert.True(
                lowestImageRow > cellHeight * 2,
                $"image was clipped to its anchor row: lowest image row {lowestImageRow}, cell height {cellHeight}");
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

    /// <summary>
    /// A glyph the font draws wider than the cells the width table gave it has to be squeezed back
    /// into them. U+26A0 is one cell by the table - and by the reckoning of whatever program laid
    /// out the screen - but no monospace font on this machine draws it that narrow: Cascadia Mono
    /// has no glyph for it and lands on Segoe UI Emoji at over two cells, HackGen has one and draws
    /// it full width. Letting it paint at its own size puts its ink in the next character's cell.
    /// </summary>
    [Fact]
    public void SurfaceKeepsAnOversizedGlyphInsideItsCell()
    {
        RunSta(() =>
        {
            var surface = CreateSurface();
            surface.UpdateSnapshot(new AnsiTerminalBuffer.TerminalRenderSnapshot([CreateLine("⚠")]));

            bool[] columns = RenderInkColumns(surface);
            int rightmost = LastInkColumn(columns);
            double cellWidth = surface.CharacterCellSize.Width;

            Assert.True(rightmost >= 0, "Expected the glyph to paint something.");
            Assert.True(
                rightmost < surface.Padding.Left + cellWidth,
                $"Ink reached column {rightmost}, past the one cell (width {cellWidth:0.##}) the glyph owns.");
        });
    }

    /// <summary>
    /// The bug this guards: a font that carries a full-width glyph for a codepoint the width table
    /// counts as one cell - HackGen does this for U+26A0, U+23BF, U+2713 and friends, all of which
    /// Claude Code puts at the head of its lines - used to shift every character after it one cell
    /// right for the rest of the segment, because the whole segment was handed to a single
    /// FormattedText that advanced by the font's widths rather than by cells.
    /// </summary>
    [Fact]
    public void SurfaceKeepsTextOnTheGridAfterAGlyphTheFontDrawsWide()
    {
        RunSta(() =>
        {
            if (!TryGetInstalledFontFamily("HackGenNerd Console", out FontFamily? family))
            {
                return;
            }

            int WithLeadingCharacter(string leading)
            {
                var surface = CreateSurface();
                surface.FontFamily = family;
                surface.UpdateSnapshot(new AnsiTerminalBuffer.TerminalRenderSnapshot(
                    [CreateLine(leading + "|")]));
                return LastInkColumn(RenderInkColumns(surface));
            }

            int reference = WithLeadingCharacter(" ");
            int afterWideGlyph = WithLeadingCharacter("⚠");

            Assert.True(reference >= 0, "Expected the reference line to paint something.");
            Assert.True(
                Math.Abs(afterWideGlyph - reference) <= 1,
                $"The '|' landed at column {afterWideGlyph} behind U+26A0 but at {reference} behind a space; "
                    + "the cell grid must not depend on what precedes a character.");
        });
    }

    private static bool TryGetInstalledFontFamily(string name, out FontFamily? family)
    {
        family = Fonts.SystemFontFamilies.FirstOrDefault(
            candidate => candidate.FamilyNames.Values.Contains(name, StringComparer.OrdinalIgnoreCase));
        return family is not null;
    }

    /// <summary>Columns of the painted surface that carry ink, i.e. anything lighter than the background.</summary>
    private static bool[] RenderInkColumns(TerminalSurfaceControl surface)
    {
        const int Width = 640;
        const int Height = 320;
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var onRender = typeof(TerminalSurfaceControl).GetMethod("OnRender", flags, [typeof(DrawingContext)])!;
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            onRender.Invoke(surface, [context]);
        }

        var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        byte[] pixels = new byte[Width * Height * 4];
        bitmap.CopyPixels(pixels, Width * 4, 0);

        var columns = new bool[Width];
        for (int y = 0; y < Height; y++)
        {
            int row = y * Width * 4;
            for (int x = 0; x < Width; x++)
            {
                int offset = row + (x * 4);
                if (pixels[offset] + pixels[offset + 1] + pixels[offset + 2] > 150)
                {
                    columns[x] = true;
                }
            }
        }

        return columns;
    }

    private static int LastInkColumn(bool[] columns)
    {
        for (int x = columns.Length - 1; x >= 0; x--)
        {
            if (columns[x])
            {
                return x;
            }
        }

        return -1;
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
