using System.Text.RegularExpressions;

namespace Terminal.Tabs;

/// <summary>One labelled target on screen: a run of text the user can pick by typing its label.</summary>
internal readonly record struct TerminalHint(int LineIndex, int Start, int Length, string Text, string Label);

/// <summary>
/// Hint mode (Quick Select): finds things worth copying on screen — URLs, paths, git hashes,
/// UUIDs, IP addresses — and gives each a short label to type. Identical texts share a label, and
/// the shortest, easiest labels go to the targets nearest the bottom (the newest output).
/// </summary>
internal static class TerminalHintFinder
{
    /// <summary>Home-row first, as in WezTerm's quick select.</summary>
    public const string Alphabet = "asdfqwerzxcvjklmiuopghtybn";

    private static readonly Regex Targets = new(
        string.Join('|',
            // URLs.
            @"\b(?:https?|ftp|file)://[^\s<>""'`]+",
            // UUIDs (before hashes, which would take their first group).
            @"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b",
            // Windows paths: drive-rooted or UNC.
            @"(?:\b[A-Za-z]:\\|\\\\)[^\s<>""'|*?]+",
            // Slash paths with at least one separator: /usr/bin, ~/x, ./a/b, src/foo.cs.
            @"(?<![\w.\-/~])(?:~|\.{1,2})?/?[\w.\-]+(?:/[\w.\-]+)+/?",
            // IPv4, optionally with a port.
            @"\b(?:\d{1,3}\.){3}\d{1,3}(?::\d{1,5})?\b",
            // Git hashes: 7-40 hex digits with at least one letter and one digit.
            @"\b(?=[0-9a-f]*[a-f])(?=[0-9a-f]*\d)[0-9a-f]{7,40}\b"),
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private const string TrailingPunctuation = ".,;:!?'\")]}>";

    /// <summary>Finds and labels the targets in <paramref name="lines"/> (absolute line index, text).</summary>
    public static IReadOnlyList<TerminalHint> Find(IEnumerable<(int LineIndex, string Text)> lines)
    {
        var found = new List<(int LineIndex, int Start, string Text)>();
        try
        {
            foreach ((int lineIndex, string text) in lines)
            {
                for (Match match = Targets.Match(text); match.Success; match = match.NextMatch())
                {
                    string value = match.Value.TrimEnd(TrailingPunctuation.ToCharArray());
                    if (value.Length >= 4)
                    {
                        found.Add((lineIndex, match.Index, value));
                    }
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // Label what was found so far rather than stall the key press.
        }

        // Nearest the bottom first, right to left within a row.
        found.Sort((a, b) => a.LineIndex != b.LineIndex ? b.LineIndex.CompareTo(a.LineIndex) : b.Start.CompareTo(a.Start));
        var uniqueTexts = found.Select(f => f.Text).Distinct(StringComparer.Ordinal).ToList();
        IReadOnlyList<string> labels = CreateLabels(uniqueTexts.Count);
        var labelByText = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int index = 0; index < labels.Count; index++)
        {
            labelByText[uniqueTexts[index]] = labels[index];
        }

        return found
            .Where(f => labelByText.ContainsKey(f.Text))
            .Select(f => new TerminalHint(f.LineIndex, f.Start, f.Text.Length, f.Text, labelByText[f.Text]))
            .ToList();
    }

    /// <summary>
    /// <paramref name="count"/> prefix-free labels: single letters while they suffice, otherwise all
    /// two-letter labels (so no label is a prefix of another). At most 26×26.
    /// </summary>
    public static IReadOnlyList<string> CreateLabels(int count)
    {
        if (count <= 0)
        {
            return [];
        }

        if (count <= Alphabet.Length)
        {
            return Alphabet.Take(count).Select(c => c.ToString()).ToList();
        }

        var labels = new List<string>(Math.Min(count, Alphabet.Length * Alphabet.Length));
        foreach (char first in Alphabet)
        {
            foreach (char second in Alphabet)
            {
                if (labels.Count == count)
                {
                    return labels;
                }

                labels.Add(string.Concat(first, second));
            }
        }

        return labels;
    }
}

/// <summary>What a key press in hint mode does.</summary>
internal enum TerminalHintKeyResult
{
    /// <summary>Still choosing: the typed prefix changed or the key was ignored.</summary>
    Continue,

    /// <summary>A hint was chosen; copy it.</summary>
    Copy,

    /// <summary>A hint was chosen with Shift; copy it and paste it into the terminal.</summary>
    CopyAndPaste,

    /// <summary>Leave hint mode without choosing.</summary>
    Cancel
}

/// <summary>The typed-label state of hint mode.</summary>
internal sealed class TerminalHintSelection
{
    private readonly IReadOnlyList<TerminalHint> _hints;

    public TerminalHintSelection(IReadOnlyList<TerminalHint> hints)
    {
        _hints = hints;
    }

    public string Typed { get; private set; } = string.Empty;

    public TerminalHint? Chosen { get; private set; }

    /// <summary>The hints whose label still starts with what was typed.</summary>
    public IEnumerable<TerminalHint> Remaining =>
        _hints.Where(hint => hint.Label.StartsWith(Typed, StringComparison.Ordinal));

    /// <summary>A letter typed: narrows the hints, and chooses one once its whole label is typed.
    /// A letter that matches no label is ignored.</summary>
    public TerminalHintKeyResult TypeLetter(char letter, bool shift)
    {
        string next = Typed + char.ToLowerInvariant(letter);
        if (!_hints.Any(hint => hint.Label.StartsWith(next, StringComparison.Ordinal)))
        {
            return TerminalHintKeyResult.Continue;
        }

        Typed = next;
        if (_hints.FirstOrDefault(hint => hint.Label == Typed) is { Label.Length: > 0 } chosen)
        {
            Chosen = chosen;
            return shift ? TerminalHintKeyResult.CopyAndPaste : TerminalHintKeyResult.Copy;
        }

        return TerminalHintKeyResult.Continue;
    }

    /// <summary>Backspace: drops the last typed letter, or leaves hint mode when nothing was typed.</summary>
    public TerminalHintKeyResult Backspace()
    {
        if (Typed.Length == 0)
        {
            return TerminalHintKeyResult.Cancel;
        }

        Typed = Typed[..^1];
        return TerminalHintKeyResult.Continue;
    }
}
