using System.Text;
using System.Windows.Media;

namespace Terminal.Buffer;

internal static class TerminalLineSnapshotBuilder
{
    public static string ExtractPlainText(TerminalLine line)
    {
        var builder = new StringBuilder(line.Cells.Length);
        foreach (TerminalCell cell in line.Cells)
        {
            if (!cell.IsContinuation)
            {
                builder.Append(cell.Text);
            }
        }

        return builder.ToString();
    }

    public static AnsiTerminalBuffer.TerminalRenderLineSnapshot CreateSnapshot(
        TerminalLine line,
        int cursorColumn,
        int anchorColumn,
        bool showCursor,
        bool screenReverse,
        Color defaultForeground,
        Color defaultBackground,
        Color cursorAccent)
    {
        int visibleLength = FindVisibleLength(line, cursorColumn);
        if (visibleLength == 0)
        {
            return new AnsiTerminalBuffer.TerminalRenderLineSnapshot(
                anchorColumn == 0 ? 0 : -1,
                0,
                []);
        }

        var text = new StringBuilder();
        var segments = new List<AnsiTerminalBuffer.TerminalRenderSegmentSnapshot>();
        ResolvedStyle? currentStyle = null;
        int currentSegmentCellLength = 0;
        int anchorSegmentIndex = -1;
        for (int column = 0; column < visibleLength; column++)
        {
            if (anchorColumn == column)
            {
                FlushSegment(segments, text, currentStyle, ref currentSegmentCellLength);
                anchorSegmentIndex = segments.Count;
            }

            TerminalCell cell = line.Cells[column];
            if (cell.IsContinuation)
            {
                continue;
            }

            bool isCursor = showCursor && cursorColumn == column;
            ResolvedStyle style = ResolveStyle(
                cell.Style,
                cell.Hyperlink,
                isCursor,
                screenReverse,
                defaultForeground,
                defaultBackground,
                cursorAccent);
            if (currentStyle is null || currentStyle.Value != style)
            {
                FlushSegment(segments, text, currentStyle, ref currentSegmentCellLength);
                currentStyle = style;
            }

            text.Append(cell.Text);
            currentSegmentCellLength += Math.Max(1, cell.Width);
        }

        FlushSegment(segments, text, currentStyle, ref currentSegmentCellLength);
        if (anchorColumn == visibleLength)
        {
            anchorSegmentIndex = segments.Count;
        }

        return new AnsiTerminalBuffer.TerminalRenderLineSnapshot(
            anchorSegmentIndex,
            visibleLength,
            segments.ToArray());
    }

    private static int FindVisibleLength(TerminalLine line, int cursorColumn)
    {
        for (int column = line.Cells.Length - 1; column >= 0; column--)
        {
            TerminalCell cell = line.Cells[column];
            if (column == cursorColumn ||
                cell.IsContinuation ||
                cell.Text != " " ||
                cell.Style != TerminalStyle.Default ||
                cell.Hyperlink is not null)
            {
                return column + 1;
            }
        }

        return cursorColumn >= 0 ? cursorColumn + 1 : 0;
    }

    private static void FlushSegment(
        List<AnsiTerminalBuffer.TerminalRenderSegmentSnapshot> segments,
        StringBuilder text,
        ResolvedStyle? style,
        ref int cellLength)
    {
        if (text.Length == 0 || style is null)
        {
            return;
        }

        segments.Add(new AnsiTerminalBuffer.TerminalRenderSegmentSnapshot(
            text.ToString(),
            cellLength,
            style.Value.Foreground,
            style.Value.Background,
            style.Value.Bold,
            style.Value.Italic,
            style.Value.UnderlineStyle,
            style.Value.UnderlineColor,
            style.Value.Strikethrough,
            style.Value.Overline,
            style.Value.Hyperlink,
            style.Value.Blink));
        text.Clear();
        cellLength = 0;
    }

    private static ResolvedStyle ResolveStyle(
        TerminalStyle style,
        string? hyperlink,
        bool isCursor,
        bool screenReverse,
        Color defaultForeground,
        Color defaultBackground,
        Color cursorAccent)
    {
        Color foreground = style.Foreground ?? defaultForeground;
        Color background = style.Background ?? defaultBackground;

        if (style.Inverse)
        {
            (foreground, background) = (background, foreground);
        }

        if (style.Dim)
        {
            foreground = DimColor(foreground);
        }

        if (style.Invisible && !isCursor)
        {
            foreground = background;
        }

        if (isCursor)
        {
            (foreground, background) = (background, foreground);
            if (foreground == background)
            {
                background = cursorAccent;
                foreground = defaultBackground;
            }
        }

        if (screenReverse)
        {
            (foreground, background) = (background, foreground);
        }

        return new ResolvedStyle(
            foreground,
            background,
            style.Bold,
            style.Italic,
            style.UnderlineStyle,
            style.UnderlineColor,
            style.Strikethrough,
            style.Overline,
            hyperlink,
            style.Blink);
    }

    private static Color DimColor(Color color) =>
        Color.FromRgb(
            (byte)Math.Round(color.R * 0.55),
            (byte)Math.Round(color.G * 0.55),
            (byte)Math.Round(color.B * 0.55));

    private readonly record struct ResolvedStyle(
        Color Foreground,
        Color Background,
        bool Bold,
        bool Italic,
        UnderlineStyle UnderlineStyle,
        Color? UnderlineColor,
        bool Strikethrough,
        bool Overline,
        string? Hyperlink,
        bool Blink);
}
