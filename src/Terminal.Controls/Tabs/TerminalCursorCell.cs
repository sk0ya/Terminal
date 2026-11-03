using System.Windows;
using System.Windows.Media;

using Terminal.Rendering;

namespace Terminal.Tabs;

/// <summary>
/// The focused block cursor: the cell under it is redrawn inverted (opaque fill, glyph in a
/// contrasting colour) like other terminals, rather than a translucent plate laid over the glyph
/// that washed it out. It is its own small element so a blink only toggles its visibility instead
/// of repainting the whole terminal surface.
/// </summary>
internal sealed class TerminalCursorCell : FrameworkElement
{
    private static readonly Brush FallbackBrush = CreateFrozenBrush(Color.FromArgb(0xA0, 0xE3, 0xE3, 0xE3));

    private TerminalSurfaceControl? _surface;
    private int _line;
    private int _column;
    private Color _fill;

    /// <summary>Points the cursor at a cell. Always repaints: the glyph under it may have changed.</summary>
    public void Show(TerminalSurfaceControl surface, int line, int column, Color fill)
    {
        _surface = surface;
        _line = line;
        _column = column;
        _fill = fill;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        Size size = RenderSize;
        if (size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        if (_surface is null || !_surface.RenderCursorCell(drawingContext, _line, _column, size, _fill))
        {
            drawingContext.DrawRectangle(FallbackBrush, null, new Rect(size));
        }
    }

    private static Brush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
