using System.Globalization;
using System.Text;

using Terminal.Buffer;

namespace Terminal.Rendering;

/// <summary>Maps UTF-16 text positions to terminal cell positions.</summary>
internal sealed class TerminalTextCellMap
{
    private readonly Entry[] _entries;

    private TerminalTextCellMap(string text, int cellLength, Entry[] entries)
    {
        Text = text;
        CellLength = cellLength;
        _entries = entries;
    }

    public string Text { get; }

    public int CellLength { get; }

    public static TerminalTextCellMap Create(string text, int targetCellLength, bool ambiguousAsWide)
    {
        ArgumentNullException.ThrowIfNull(text);
        targetCellLength = Math.Max(0, targetCellLength);
        if (text.Length == 0)
        {
            return new TerminalTextCellMap(text, targetCellLength, []);
        }

        int[] starts = StringInfo.ParseCombiningCharacters(text);
        var entries = new Entry[starts.Length];
        int totalCells = 0;
        for (int index = 0; index < starts.Length; index++)
        {
            int start = starts[index];
            int end = index + 1 < starts.Length ? starts[index + 1] : text.Length;
            int cellLength = TerminalWidthCalculator.EstimateGraphemeWidth(
                text.AsSpan(start, end - start),
                ambiguousAsWide);
            entries[index] = new Entry(start, end - start, totalCells, cellLength);
            totalCells += cellLength;
        }

        if (totalCells != targetCellLength)
        {
            Entry last = entries[^1];
            entries[^1] = last with
            {
                CellLength = Math.Max(1, last.CellLength + (targetCellLength - totalCells))
            };
        }

        return new TerminalTextCellMap(text, targetCellLength, entries);
    }

    public static TerminalTextCellMap Create(
        IEnumerable<(string Text, int CellLength)> segments,
        int targetCellLength,
        bool ambiguousAsWide)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var text = new StringBuilder();
        var entries = new List<Entry>();
        int textOffset = 0;
        int cellOffset = 0;
        foreach ((string segmentText, int segmentCellLength) in segments)
        {
            TerminalTextCellMap segment = Create(segmentText, segmentCellLength, ambiguousAsWide);
            text.Append(segmentText);
            entries.AddRange(segment._entries.Select(entry => new Entry(
                entry.TextIndex + textOffset,
                entry.TextLength,
                entry.StartCell + cellOffset,
                entry.CellLength)));
            textOffset += segmentText.Length;
            cellOffset += segmentCellLength;
        }

        if (entries.Count > 0)
        {
            int mappedCellLength = entries.Sum(static entry => entry.CellLength);
            if (mappedCellLength != targetCellLength)
            {
                Entry last = entries[^1];
                entries[^1] = last with
                {
                    CellLength = Math.Max(1, last.CellLength + (targetCellLength - mappedCellLength))
                };
            }
        }

        return new TerminalTextCellMap(text.ToString(), Math.Max(0, targetCellLength), entries.ToArray());
    }

    public int GetTextIndex(double cellPosition)
    {
        if (_entries.Length == 0 || cellPosition <= 0)
        {
            return 0;
        }

        if (cellPosition >= CellLength)
        {
            return Text.Length;
        }

        foreach (Entry entry in _entries)
        {
            if (cellPosition < entry.StartCell)
            {
                return entry.TextIndex;
            }

            double endCell = entry.StartCell + entry.CellLength;
            if (cellPosition <= endCell)
            {
                double midpoint = entry.StartCell + (entry.CellLength / 2.0);
                return cellPosition >= midpoint ? entry.TextIndex + entry.TextLength : entry.TextIndex;
            }
        }

        return Text.Length;
    }

    public int GetCellColumn(int textIndex, bool preferTrailingEdge)
    {
        if (textIndex <= 0 || _entries.Length == 0)
        {
            return 0;
        }

        if (textIndex >= Text.Length)
        {
            return CellLength;
        }

        foreach (Entry entry in _entries)
        {
            if (textIndex < entry.TextIndex)
            {
                return entry.StartCell;
            }

            int entryEnd = entry.TextIndex + entry.TextLength;
            if (textIndex < entryEnd)
            {
                return preferTrailingEdge ? entry.StartCell + entry.CellLength : entry.StartCell;
            }

            if (textIndex == entryEnd)
            {
                return entry.StartCell + entry.CellLength;
            }
        }

        return CellLength;
    }

    private readonly record struct Entry(int TextIndex, int TextLength, int StartCell, int CellLength);
}

internal readonly record struct TerminalSurfaceCoordinateMapper(
    double CellWidth,
    double CellHeight,
    double PaddingLeft,
    double PaddingTop,
    double HorizontalOffset,
    double VerticalOffset)
{
    public double GetCellColumn(double x) =>
        Math.Max(0, x - PaddingLeft + HorizontalOffset) / CellWidth;

    public int GetLineIndex(double y, int lineCount) => lineCount <= 0
        ? 0
        : Math.Clamp((int)(Math.Max(0, y - PaddingTop + VerticalOffset) / CellHeight), 0, lineCount - 1);
}
