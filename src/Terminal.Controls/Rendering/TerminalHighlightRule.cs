using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace Terminal.Rendering;

/// <summary>
/// Text in the terminal matching <see cref="Pattern"/> gets a tinted background of <see cref="Color"/>
/// (iTerm2-style "triggers", display only). Matching is per row; a match never spans rows.
/// </summary>
public sealed class TerminalHighlightRule
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(50);

    public TerminalHighlightRule(Regex pattern, Color color)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        Pattern = pattern;
        Color = color;
    }

    public Regex Pattern { get; }

    public Color Color { get; }

    /// <summary>
    /// Parses settings lines of the form <c>#RRGGBB pattern</c> (or <c>#AARRGGBB pattern</c>). Blank
    /// lines and lines starting with <c>//</c> are skipped; a line whose colour or regular expression
    /// does not parse is reported in <paramref name="errors"/> and skipped. Patterns are
    /// case-insensitive unless they start with <c>(?-i)</c>.
    /// </summary>
    public static IReadOnlyList<TerminalHighlightRule> Parse(IEnumerable<string>? lines, out IReadOnlyList<string> errors)
    {
        var rules = new List<TerminalHighlightRule>();
        var problems = new List<string>();
        foreach (string raw in lines ?? [])
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            int space = line.IndexOfAny([' ', '\t']);
            string colorText = space < 0 ? line : line[..space];
            string pattern = space < 0 ? string.Empty : line[(space + 1)..].Trim();
            if (pattern.Length == 0 || !TryParseColor(colorText, out Color color))
            {
                problems.Add($"Expected \"#RRGGBB pattern\": {line}");
                continue;
            }

            try
            {
                rules.Add(new TerminalHighlightRule(
                    new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout),
                    color));
            }
            catch (ArgumentException ex)
            {
                problems.Add($"Invalid pattern \"{pattern}\": {ex.Message}");
            }
        }

        errors = problems;
        return rules;
    }

    private static bool TryParseColor(string text, out Color color)
    {
        color = default;
        if (!text.StartsWith('#') || (text.Length != 7 && text.Length != 9))
        {
            return false;
        }

        try
        {
            color = (Color)ColorConverter.ConvertFromString(text);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>A highlighted text range on one row, in text indices, with the rule's colour.</summary>
internal readonly record struct TerminalHighlightSpan(int Start, int End, Color Color);

/// <summary>
/// Finds and caches the highlight spans of row texts. Keyed by the row's text instance, so a row
/// whose content did not change is not matched again on the next frame.
/// </summary>
internal sealed class TerminalHighlighter
{
    private IReadOnlyList<TerminalHighlightRule> _rules = [];
    private ConditionalWeakTable<string, TerminalHighlightSpan[]> _cache = new();

    public bool HasRules => _rules.Count > 0;

    public void SetRules(IReadOnlyList<TerminalHighlightRule> rules)
    {
        _rules = rules ?? [];
        _cache = new ConditionalWeakTable<string, TerminalHighlightSpan[]>();
    }

    public TerminalHighlightSpan[] GetSpans(string text)
    {
        if (_rules.Count == 0 || string.IsNullOrEmpty(text))
        {
            return [];
        }

        return _cache.GetValue(text, Compute);
    }

    private TerminalHighlightSpan[] Compute(string text)
    {
        var spans = new List<TerminalHighlightSpan>();
        foreach (TerminalHighlightRule rule in _rules)
        {
            try
            {
                for (Match match = rule.Pattern.Match(text); match.Success; match = match.NextMatch())
                {
                    if (match.Length > 0)
                    {
                        spans.Add(new TerminalHighlightSpan(match.Index, match.Index + match.Length, rule.Color));
                    }
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // A pathological rule must not stall rendering; it just does not highlight this row.
            }
        }

        return spans.ToArray();
    }
}
