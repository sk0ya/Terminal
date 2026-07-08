using System.Text;

using Terminal.Tabs;

namespace Terminal.Rendering;

internal readonly record struct TerminalTextPosition(int LineIndex, int TextIndex) : IComparable<TerminalTextPosition>
{
    public int CompareTo(TerminalTextPosition other)
    {
        int lineCompare = LineIndex.CompareTo(other.LineIndex);
        return lineCompare != 0 ? lineCompare : TextIndex.CompareTo(other.TextIndex);
    }
}

internal readonly record struct TerminalTextRange(TerminalTextPosition Start, TerminalTextPosition End)
{
    public bool IsEmpty => Start == End;
}

internal readonly record struct TerminalSelectionLine(string Text, TerminalTextCellMap TextCellMap);

/// <summary>
/// Pure selection and search decisions for a terminal text snapshot. This type deliberately knows
/// nothing about WPF input, drawing, scrolling, or dispatcher affinity.
/// </summary>
internal sealed class TerminalSelectionSearchModel
{
    public TerminalTextRange? Selection { get; set; }

    public bool IsBlockSelection { get; set; }

    public double BlockAnchorColumn { get; set; }

    public double BlockCurrentColumn { get; set; }

    public bool HasSelection => Normalize(Selection) is { IsEmpty: false };

    public void ClearSelection()
    {
        Selection = null;
        IsBlockSelection = false;
    }

    public static TerminalTextRange? Normalize(TerminalTextRange? selection)
    {
        if (!selection.HasValue)
        {
            return null;
        }

        TerminalTextRange range = selection.Value;
        return range.Start.CompareTo(range.End) <= 0
            ? range
            : new TerminalTextRange(range.End, range.Start);
    }

    public static TerminalTextPosition ClampPosition(
        IReadOnlyList<TerminalSelectionLine> lines,
        TerminalTextPosition position)
    {
        if (lines.Count == 0)
        {
            return new TerminalTextPosition(0, 0);
        }

        int lineIndex = Math.Clamp(position.LineIndex, 0, lines.Count - 1);
        return new TerminalTextPosition(
            lineIndex,
            Math.Clamp(position.TextIndex, 0, lines[lineIndex].Text.Length));
    }

    public static TerminalTextRange? ClampRange(
        IReadOnlyList<TerminalSelectionLine> lines,
        TerminalTextRange? selection)
    {
        if (!selection.HasValue || lines.Count == 0)
        {
            return null;
        }

        TerminalTextRange range = selection.Value;
        return new TerminalTextRange(
            ClampPosition(lines, range.Start),
            ClampPosition(lines, range.End));
    }

    public static bool TryCreateMatchRange(
        IReadOnlyList<TerminalSelectionLine> lines,
        int lineIndex,
        int column,
        int length,
        out TerminalTextRange range)
    {
        range = default;
        if (lineIndex < 0 || lineIndex >= lines.Count)
        {
            return false;
        }

        string text = lines[lineIndex].Text;
        int start = Math.Clamp(column, 0, text.Length);
        long requestedEnd = (long)column + length;
        int end = (int)Math.Clamp(requestedEnd, start, text.Length);
        range = new TerminalTextRange(
            new TerminalTextPosition(lineIndex, start),
            new TerminalTextPosition(lineIndex, end));
        return true;
    }

    public static IReadOnlyList<TerminalMatch> FindMatches(
        IReadOnlyList<TerminalSelectionLine> lines,
        string query,
        StringComparison comparison)
    {
        var matches = new List<TerminalMatch>();
        if (string.IsNullOrEmpty(query))
        {
            return matches;
        }

        for (int lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            string text = lines[lineIndex].Text;
            for (int index = 0; index <= text.Length;)
            {
                int found = text.IndexOf(query, index, comparison);
                if (found < 0)
                {
                    break;
                }

                matches.Add(new TerminalMatch(lineIndex, found, query.Length, text));
                index = found + query.Length;
            }
        }

        return matches;
    }

    public static int CountMatches(
        IReadOnlyList<TerminalSelectionLine> lines,
        string query,
        StringComparison comparison)
    {
        if (string.IsNullOrEmpty(query))
        {
            return 0;
        }

        int count = 0;
        for (int lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            string text = lines[lineIndex].Text;
            for (int index = 0; index < text.Length;)
            {
                int found = text.IndexOf(query, index, comparison);
                if (found < 0)
                {
                    break;
                }

                count++;
                index = found + query.Length;
            }
        }

        return count;
    }

    public static bool TryFindNext(
        IReadOnlyList<TerminalSelectionLine> lines,
        TerminalTextRange? selection,
        string query,
        StringComparison comparison,
        bool forward,
        out TerminalTextRange match,
        out bool wrapped)
    {
        match = default;
        wrapped = false;
        if (string.IsNullOrEmpty(query) || lines.Count == 0)
        {
            return false;
        }

        TerminalTextRange? normalized = Normalize(selection);
        TerminalTextPosition start = forward
            ? normalized?.End ?? new TerminalTextPosition(0, 0)
            : normalized?.Start ?? new TerminalTextPosition(lines.Count - 1, lines[^1].Text.Length);

        if (forward ? TryFindForward(lines, start, query, comparison, out match)
                    : TryFindBackward(lines, start, query, comparison, out match))
        {
            return true;
        }

        TerminalTextPosition wrapStart = forward
            ? new TerminalTextPosition(0, 0)
            : new TerminalTextPosition(lines.Count - 1, lines[^1].Text.Length);
        wrapped = forward ? TryFindForward(lines, wrapStart, query, comparison, out match)
                          : TryFindBackward(lines, wrapStart, query, comparison, out match);
        return wrapped;
    }

    public static string ExtractText(
        IReadOnlyList<TerminalSelectionLine> lines,
        TerminalTextRange? selection,
        bool blockSelection,
        double blockAnchorColumn = 0,
        double blockCurrentColumn = 0)
    {
        TerminalTextRange? clamped = ClampRange(lines, Normalize(selection));
        if (!clamped.HasValue)
        {
            return string.Empty;
        }

        TerminalTextRange range = Normalize(clamped)!.Value;
        (int left, int right) = GetBlockColumns(blockAnchorColumn, blockCurrentColumn);
        var builder = new StringBuilder();
        for (int lineIndex = range.Start.LineIndex; lineIndex <= range.End.LineIndex; lineIndex++)
        {
            TerminalSelectionLine line = lines[lineIndex];
            int start = blockSelection
                ? line.TextCellMap.GetTextIndex(left)
                : lineIndex == range.Start.LineIndex ? range.Start.TextIndex : 0;
            int end = blockSelection
                ? line.TextCellMap.GetTextIndex(right)
                : lineIndex == range.End.LineIndex ? range.End.TextIndex : line.Text.Length;
            start = Math.Clamp(start, 0, line.Text.Length);
            end = Math.Clamp(end, start, line.Text.Length);
            builder.Append(line.Text.AsSpan(start, end - start));
            if (lineIndex < range.End.LineIndex)
            {
                builder.AppendLine();
            }
        }

        return builder.ToString();
    }

    public static (int Left, int Right) GetBlockColumns(double anchorColumn, double currentColumn)
    {
        double normalizedAnchor = NormalizeColumn(anchorColumn);
        double normalizedCurrent = NormalizeColumn(currentColumn);
        int left = ClampToInt(Math.Floor(Math.Min(normalizedAnchor, normalizedCurrent)));
        int right = ClampToInt(Math.Ceiling(Math.Max(normalizedAnchor, normalizedCurrent)));
        if (right > left)
        {
            return (left, right);
        }

        // Preserve a one-cell selection without overflowing at Int32.MaxValue.
        return left < int.MaxValue
            ? (left, left + 1)
            : (int.MaxValue - 1, int.MaxValue);
    }

    private static double NormalizeColumn(double value) => value switch
    {
        double.NaN => 0,
        double.NegativeInfinity => int.MinValue,
        double.PositiveInfinity => int.MaxValue,
        _ => Math.Clamp(value, int.MinValue, int.MaxValue)
    };

    private static int ClampToInt(double value) => value <= int.MinValue
        ? int.MinValue
        : value >= int.MaxValue
            ? int.MaxValue
            : (int)value;

    private static bool TryFindForward(IReadOnlyList<TerminalSelectionLine> lines, TerminalTextPosition start,
        string query, StringComparison comparison, out TerminalTextRange range)
    {
        start = ClampPosition(lines, start);
        for (int lineIndex = start.LineIndex; lineIndex < lines.Count; lineIndex++)
        {
            string text = lines[lineIndex].Text;
            int searchStart = lineIndex == start.LineIndex ? start.TextIndex : 0;
            int found = text.IndexOf(query, searchStart, comparison);
            if (found >= 0)
            {
                range = CreateRange(lineIndex, found, query.Length);
                return true;
            }
        }

        range = default;
        return false;
    }

    private static bool TryFindBackward(IReadOnlyList<TerminalSelectionLine> lines, TerminalTextPosition start,
        string query, StringComparison comparison, out TerminalTextRange range)
    {
        start = ClampPosition(lines, start);
        for (int lineIndex = start.LineIndex; lineIndex >= 0; lineIndex--)
        {
            string text = lines[lineIndex].Text;
            int limit = lineIndex == start.LineIndex ? start.TextIndex : text.Length;
            for (int index = limit - query.Length; index >= 0; index--)
            {
                if (string.Compare(text, index, query, 0, query.Length, comparison) == 0)
                {
                    range = CreateRange(lineIndex, index, query.Length);
                    return true;
                }
            }
        }

        range = default;
        return false;
    }

    private static TerminalTextRange CreateRange(int lineIndex, int start, int length) => new(
        new TerminalTextPosition(lineIndex, start),
        new TerminalTextPosition(lineIndex, start + length));
}
