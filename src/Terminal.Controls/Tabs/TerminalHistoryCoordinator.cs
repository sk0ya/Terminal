namespace Terminal.Tabs;

internal readonly record struct TerminalHistoryResult(
    string Command,
    string Display,
    IReadOnlyList<int> MatchedIndices);

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
