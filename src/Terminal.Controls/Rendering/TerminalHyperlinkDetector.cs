using System.Text.RegularExpressions;

namespace Terminal.Rendering;

/// <summary>Resolves explicit and inferred hyperlinks without depending on WPF.</summary>
internal static class TerminalHyperlinkDetector
{
    private static readonly Regex UrlPattern = new(
        @"(?:https?|ftp|file)://[^\s<>""'` ]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FilePathPattern = new(
        @"(?:[A-Za-z]:[\\/]|\\\\[^\s\\/]+[\\/]|\.{0,2}[\\/])[^\s:*?""<>|]+(?:[\\/][^\s:*?""<>|]+)*(?::\d+(?::\d+)?)?" +
        @"|[\w.\-]+(?:[\\/][\w.\-]+)+\.\w+(?::\d+(?::\d+)?)?",
        RegexOptions.Compiled);

    public static bool TryResolve(
        string text,
        TerminalTextCellMap textCellMap,
        IReadOnlyList<TerminalHyperlinkSegment> explicitSegments,
        int textIndex,
        out TerminalHyperlinkMatch match)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(textCellMap);
        ArgumentNullException.ThrowIfNull(explicitSegments);

        match = default;
        if (textIndex < 0 || textIndex >= text.Length)
        {
            return false;
        }

        int cellColumn = textCellMap.GetCellColumn(textIndex, preferTrailingEdge: false);
        foreach (TerminalHyperlinkSegment segment in explicitSegments)
        {
            if (segment.Target is null || segment.CellLength <= 0 ||
                cellColumn < segment.StartCell || cellColumn >= segment.StartCell + segment.CellLength)
            {
                continue;
            }

            match = new TerminalHyperlinkMatch(
                segment.Target,
                segment.StartCell,
                segment.StartCell + segment.CellLength);
            return true;
        }

        if (!TryDetectTargetAt(text, textIndex, out string? target, out int start, out int length))
        {
            return false;
        }

        match = new TerminalHyperlinkMatch(
            target!,
            textCellMap.GetCellColumn(start, preferTrailingEdge: false),
            textCellMap.GetCellColumn(start + length, preferTrailingEdge: false));
        return true;
    }

    internal static bool TryDetectTargetAt(
        string text,
        int textIndex,
        out string? target,
        out int matchStart,
        out int matchLength)
    {
        ArgumentNullException.ThrowIfNull(text);
        target = null;
        matchStart = 0;
        matchLength = 0;
        if (text.Length == 0 || textIndex < 0 || textIndex >= text.Length)
        {
            return false;
        }

        // A URL can also resemble a path. Preserve URL precedence independently of regex order.
        return TryMatchAt(UrlPattern, text, textIndex, out target, out matchStart, out matchLength)
            || TryMatchAt(FilePathPattern, text, textIndex, out target, out matchStart, out matchLength);
    }

    private static bool TryMatchAt(
        Regex pattern,
        string text,
        int textIndex,
        out string? target,
        out int matchStart,
        out int matchLength)
    {
        target = null;
        matchStart = 0;
        matchLength = 0;
        foreach (Match candidate in pattern.Matches(text))
        {
            int length = TrimTrailingPunctuation(candidate.Value);
            if (length <= 0 || textIndex < candidate.Index || textIndex >= candidate.Index + length)
            {
                continue;
            }

            target = text.Substring(candidate.Index, length);
            matchStart = candidate.Index;
            matchLength = length;
            return true;
        }

        return false;
    }

    private static int TrimTrailingPunctuation(string value)
    {
        int length = value.Length;
        while (length > 0 && value[length - 1] is
               '.' or ',' or ';' or ':' or '!' or '?' or ')' or ']' or '}' or '>' or '"' or '\'')
        {
            length--;
        }

        return length;
    }
}

internal readonly record struct TerminalHyperlinkSegment(int StartCell, int CellLength, string? Target);

internal readonly record struct TerminalHyperlinkMatch(string Target, int StartColumn, int EndColumn);
