namespace Terminal.Tabs;

internal enum TerminalHistoryKey
{
    Other,
    Escape,
    Enter,
    Up,
    Down,
    N,
    P,
    R
}

[Flags]
internal enum TerminalHistoryKeyModifiers
{
    None = 0,
    Control = 1,
    Shift = 2,
    Alt = 4,
    Windows = 8
}

internal enum TerminalHistoryKeyActionKind
{
    None,
    Close,
    Accept,
    MoveSelection
}

internal readonly record struct TerminalHistoryKeyAction(
    TerminalHistoryKeyActionKind Kind,
    int SelectionDelta = 0)
{
    public bool Handled => Kind != TerminalHistoryKeyActionKind.None;
}

internal readonly record struct TerminalHistoryResult(
    string Command,
    string Display,
    IReadOnlyList<int> MatchedIndices);

internal readonly record struct TerminalHistoryDisplaySegment(
    string Text,
    bool Highlighted);

internal sealed class TerminalHistoryCoordinator(int limit)
{
    private readonly int _limit = Math.Max(0, limit);
    private readonly List<string> _history = [];

    public IReadOnlyList<string> History => _history;
    public IReadOnlyList<TerminalHistoryResult> Results { get; private set; } = [];
    public int SelectedIndex { get; private set; } = -1;
    public bool IsSeeded { get; private set; }
    public string CountText => $"{Results.Count}/{_history.Count}";

    public bool Record(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        int existing = _history.LastIndexOf(command);
        if (existing >= 0 && existing == _history.Count - 1)
        {
            return false;
        }

        if (existing >= 0)
        {
            _history.RemoveAt(existing);
        }

        _history.Add(command);
        TrimToLimit();
        return true;
    }

    public void MarkSeeded()
    {
        IsSeeded = true;
    }

    public bool TryBeginSeed()
    {
        if (IsSeeded)
        {
            return false;
        }

        IsSeeded = true;
        return true;
    }

    public void SeedOnce(bool enabled, Func<IReadOnlyList<string>> loadHistory)
    {
        ArgumentNullException.ThrowIfNull(loadHistory);

        if (!TryBeginSeed() || !enabled)
        {
            return;
        }

        IReadOnlyList<string> past = loadHistory();
        if (past.Count > 0)
        {
            MergeSeedHistory(past);
        }
    }

    public void MergeSeedHistory(IReadOnlyList<string> olderFirst)
    {
        var combined = new List<string>(olderFirst.Count + _history.Count);
        combined.AddRange(olderFirst);
        combined.AddRange(_history);

        var seen = new HashSet<string>();
        _history.Clear();
        for (int index = combined.Count - 1; index >= 0; index--)
        {
            string command = combined[index];
            if (!string.IsNullOrWhiteSpace(command) && seen.Add(command))
            {
                _history.Add(command);
            }
        }

        _history.Reverse();
        TrimToLimit();
    }

    public void Search(string query)
    {
        bool showAll = string.IsNullOrWhiteSpace(query);
        var ranked = new List<(int Score, int Recency, TerminalHistoryResult Result)>();
        for (int index = 0; index < _history.Count; index++)
        {
            string command = _history[index];
            string display = command.ReplaceLineEndings("⏎");
            if (showAll)
            {
                ranked.Add((0, index, new TerminalHistoryResult(command, display, [])));
            }
            else if (TryFuzzyMatch(display, query, out int score, out IReadOnlyList<int> matches))
            {
                ranked.Add((score, index, new TerminalHistoryResult(command, display, matches)));
            }
        }

        ranked.Sort(static (left, right) =>
        {
            int byScore = right.Score.CompareTo(left.Score);
            return byScore != 0 ? byScore : right.Recency.CompareTo(left.Recency);
        });
        Results = ranked.AsEnumerable().Reverse().Select(item => item.Result).ToArray();
        SelectedIndex = Results.Count > 0 ? Results.Count - 1 : -1;
    }

    public int MoveSelection(int delta)
    {
        if (Results.Count == 0)
        {
            SelectedIndex = -1;
            return -1;
        }

        SelectedIndex = Math.Clamp(SelectedIndex + delta, 0, Results.Count - 1);
        return SelectedIndex;
    }

    public void SelectIndex(int index)
    {
        SelectedIndex = Results.Count == 0 || index < 0
            ? -1
            : Math.Clamp(index, 0, Results.Count - 1);
    }

    public string? AcceptSelection() =>
        SelectedIndex >= 0 && SelectedIndex < Results.Count
            ? Results[SelectedIndex].Command
            : null;

    public static IReadOnlyList<TerminalHistoryDisplaySegment> BuildDisplaySegments(
        string display,
        IReadOnlyList<int> matchedIndices)
    {
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(matchedIndices);

        if (matchedIndices.Count == 0)
        {
            return [new(display, Highlighted: false)];
        }

        if (display.Length == 0)
        {
            return [];
        }

        var matched = new HashSet<int>(matchedIndices);
        var segments = new List<TerminalHistoryDisplaySegment>();
        int segmentStart = 0;
        bool segmentHighlighted = matched.Contains(0);
        for (int index = 1; index < display.Length; index++)
        {
            bool highlighted = matched.Contains(index);
            if (highlighted == segmentHighlighted)
            {
                continue;
            }

            segments.Add(new(
                display[segmentStart..index],
                segmentHighlighted));
            segmentStart = index;
            segmentHighlighted = highlighted;
        }

        segments.Add(new(display[segmentStart..], segmentHighlighted));
        return segments;
    }

    public static TerminalHistoryKeyAction ResolveKey(
        TerminalHistoryKey key,
        TerminalHistoryKeyModifiers modifiers)
    {
        bool control = (modifiers & TerminalHistoryKeyModifiers.Control) != 0;
        return key switch
        {
            TerminalHistoryKey.Escape => new(TerminalHistoryKeyActionKind.Close),
            TerminalHistoryKey.Enter => new(TerminalHistoryKeyActionKind.Accept),
            TerminalHistoryKey.Down => new(TerminalHistoryKeyActionKind.MoveSelection, 1),
            TerminalHistoryKey.Up => new(TerminalHistoryKeyActionKind.MoveSelection, -1),
            TerminalHistoryKey.N when control => new(TerminalHistoryKeyActionKind.MoveSelection, 1),
            TerminalHistoryKey.P when control => new(TerminalHistoryKeyActionKind.MoveSelection, -1),
            TerminalHistoryKey.R when control => new(TerminalHistoryKeyActionKind.MoveSelection, -1),
            _ => default
        };
    }

    public static bool TryFuzzyMatch(
        string text,
        string query,
        out int score,
        out IReadOnlyList<int> matchedIndices)
    {
        score = 0;
        var indices = new List<int>(query.Length);
        matchedIndices = indices;
        int textIndex = 0;
        int previousMatch = -2;
        int consecutive = 0;

        foreach (char rawQueryChar in query)
        {
            if (char.IsWhiteSpace(rawQueryChar))
            {
                continue;
            }

            char queryChar = char.ToLowerInvariant(rawQueryChar);
            bool found = false;
            for (; textIndex < text.Length; textIndex++)
            {
                if (char.ToLowerInvariant(text[textIndex]) != queryChar)
                {
                    continue;
                }

                int charScore = 1;
                if (textIndex == previousMatch + 1)
                {
                    consecutive++;
                    charScore += 5 + consecutive;
                }
                else
                {
                    consecutive = 0;
                }

                if (textIndex == 0)
                {
                    charScore += 8;
                }
                else if (IsWordBoundary(text[textIndex - 1]))
                {
                    charScore += 6;
                }

                indices.Add(textIndex);
                score += charScore;
                previousMatch = textIndex;
                textIndex++;
                found = true;
                break;
            }

            if (!found)
            {
                score = 0;
                return false;
            }
        }

        return indices.Count > 0;
    }

    private void TrimToLimit()
    {
        if (_history.Count > _limit)
        {
            _history.RemoveRange(0, _history.Count - _limit);
        }
    }

    private static bool IsWordBoundary(char value) =>
        value is ' ' or '/' or '\\' or '-' or '_' or '.' or ':' or '\t';
}
