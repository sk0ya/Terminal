namespace Terminal.Tabs;

internal enum TerminalFindStatus
{
    EmptyQuery,
    NoMatch,
    Match
}

internal sealed class TerminalFindCoordinator
{
    public string Query { get; private set; } = string.Empty;
    public StringComparison Comparison { get; private set; } = StringComparison.OrdinalIgnoreCase;
    public IReadOnlyList<TerminalMatch> Matches { get; private set; } = [];
    public int CurrentIndex { get; private set; } = -1;
    public int AnchorLine { get; private set; }
    public int AnchorColumn { get; private set; }

    public TerminalFindStatus Status => string.IsNullOrEmpty(Query)
        ? TerminalFindStatus.EmptyQuery
        : Matches.Count == 0 ? TerminalFindStatus.NoMatch : TerminalFindStatus.Match;

    public TerminalMatch? CurrentMatch =>
        CurrentIndex >= 0 && CurrentIndex < Matches.Count ? Matches[CurrentIndex] : null;

    public string PositionText => Status switch
    {
        TerminalFindStatus.EmptyQuery => "Type to search",
        TerminalFindStatus.NoMatch => "No match",
        _ => TerminalFindNavigator.FormatPosition(CurrentIndex, Matches.Count)
    };

    public void Open()
    {
        AnchorLine = 0;
        AnchorColumn = 0;
    }

    public void Close()
    {
        Matches = [];
        CurrentIndex = -1;
    }

    public bool UpdateCriteria(string query, bool caseSensitive)
    {
        Query = query ?? string.Empty;
        Comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (Query.Length > 0)
        {
            return true;
        }

        Matches = [];
        CurrentIndex = -1;
        return false;
    }

    public void Refresh(IReadOnlyList<TerminalMatch> matches, bool reseek)
    {
        Matches = matches ?? [];
        if (Matches.Count == 0)
        {
            CurrentIndex = -1;
            return;
        }

        CurrentIndex = reseek
            ? TerminalFindNavigator.SeedIndex(Matches, AnchorLine, AnchorColumn, forward: true)
            : Math.Clamp(CurrentIndex, 0, Matches.Count - 1);
    }

    public void Move(IReadOnlyList<TerminalMatch> matches, bool forward)
    {
        Matches = matches ?? [];
        CurrentIndex = TerminalFindNavigator.Advance(CurrentIndex, Matches.Count, forward);
    }

    public void MarkCurrentMatchApplied()
    {
        if (CurrentMatch is not TerminalMatch match)
        {
            return;
        }

        AnchorLine = match.LineIndex;
        AnchorColumn = match.Column;
    }

    public void RefreshAfterOutputChange(IReadOnlyList<TerminalMatch> matches)
    {
        Matches = matches ?? [];
        CurrentIndex = Matches.Count == 0
            ? -1
            : CurrentIndex < 0 ? 0 : Math.Clamp(CurrentIndex, 0, Matches.Count - 1);
    }
}
