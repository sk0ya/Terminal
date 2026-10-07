using System.Globalization;
using System.Windows;
using System.Windows.Media;

using Terminal.Tabs;

namespace Terminal.Rendering;

/// <summary>Hint mode drawing: each target is tinted and its label painted over its first cells.</summary>
public partial class TerminalSurfaceControl
{
    private static readonly Brush HintTargetBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x50, 0xF7, 0xD1, 0x54)));
    private static readonly Brush HintLabelBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF7, 0xD1, 0x54)));
    private static readonly Brush HintLabelTextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x1A, 0x16, 0x12)));
    private static readonly Brush HintTypedTextBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x80, 0x1A, 0x16, 0x12)));

    private IReadOnlyList<TerminalHint> _hints = [];
    private string _hintTyped = string.Empty;

    /// <summary>Shows <paramref name="hints"/> (already narrowed to <paramref name="typed"/>); empty hides them.</summary>
    internal void SetHints(IReadOnlyList<TerminalHint> hints, string typed)
    {
        _hints = hints;
        _hintTyped = typed;
        InvalidateVisual();
    }

    /// <summary>The rows currently in the viewport, as (line index, plain text).</summary>
    internal IReadOnlyList<(int LineIndex, string Text)> GetVisibleLineTexts()
    {
        EnsureMetrics();
        if (_lines.Count == 0 || _cellSize.Height <= 0)
        {
            return [];
        }

        TerminalScrollLineWindow window = _scrollState.GetLineWindow(_lines.Count, _cellSize.Height, Padding.Top);
        var lines = new List<(int, string)>(window.LastVisibleLine - window.FirstVisibleLine + 1);
        for (int lineIndex = window.FirstVisibleLine; lineIndex <= window.LastVisibleLine; lineIndex++)
        {
            lines.Add((lineIndex, _lines[lineIndex].Text));
        }

        return lines;
    }

    private void DrawHints(DrawingContext drawingContext, int firstVisibleLine, int lastVisibleLine, double contentTop, double contentLeft)
    {
        if (_hints.Count == 0)
        {
            return;
        }

        var boldTypeface = new Typeface(FontFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        foreach (TerminalHint hint in _hints)
        {
            if (hint.LineIndex < firstVisibleLine || hint.LineIndex > lastVisibleLine)
            {
                continue;
            }

            TerminalLineLayout line = _lines[hint.LineIndex];
            int startColumn = line.TextCellMap.GetCellColumn(hint.Start, preferTrailingEdge: false);
            int endColumn = line.TextCellMap.GetCellColumn(hint.Start + hint.Length, preferTrailingEdge: true);
            double top = contentTop + (hint.LineIndex * _cellSize.Height);
            double left = contentLeft + (startColumn * _cellSize.Width);
            drawingContext.DrawRectangle(
                HintTargetBrush,
                null,
                new Rect(left, top, Math.Max(1, endColumn - startColumn) * _cellSize.Width, _cellSize.Height));

            drawingContext.DrawRectangle(
                HintLabelBrush,
                null,
                new Rect(left, top, hint.Label.Length * _cellSize.Width, _cellSize.Height));
            for (int index = 0; index < hint.Label.Length; index++)
            {
                var glyph = new FormattedText(
                    hint.Label[index].ToString(),
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    boldTypeface,
                    FontSize,
                    index < _hintTyped.Length ? HintTypedTextBrush : HintLabelTextBrush,
                    _pixelsPerDip);
                double x = left + (index * _cellSize.Width) + Math.Max(0, (_cellSize.Width - glyph.WidthIncludingTrailingWhitespace) / 2);
                drawingContext.DrawText(glyph, new Point(x, top + Math.Max(0, (_cellSize.Height - glyph.Height) / 2)));
            }
        }
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
