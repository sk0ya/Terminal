namespace Terminal.Tabs;

/// <summary>A folded run of buffer lines, <c>[Start, End)</c>, shown as a single summary line.</summary>
internal readonly record struct TerminalFold(int Start, int End)
{
    public int LineCount => End - Start;
}

/// <summary>
/// Maps between buffer lines and the lines the surface shows once folds are collapsed. Each fold
/// occupies one display line (its summary) in place of its <see cref="TerminalFold.LineCount"/>
/// buffer lines. Folds are sorted and never overlap.
/// </summary>
internal sealed class TerminalFoldMap
{
    public static readonly TerminalFoldMap Empty = new([]);

    private readonly TerminalFold[] _folds;

    public TerminalFoldMap(IEnumerable<TerminalFold> folds)
    {
        var sorted = new List<TerminalFold>();
        foreach (TerminalFold fold in folds.Where(f => f.LineCount >= 2).OrderBy(f => f.Start))
        {
            // Overlaps cannot come from distinct commands, but a stale fold might; keep the first.
            if (sorted.Count == 0 || fold.Start >= sorted[^1].End)
            {
                sorted.Add(fold);
            }
        }

        _folds = sorted.ToArray();
    }

    public IReadOnlyList<TerminalFold> Folds => _folds;

    public bool IsEmpty => _folds.Length == 0;

    /// <summary>How many buffer lines the folds remove from the display in total.</summary>
    public int HiddenLineCount => _folds.Sum(f => f.LineCount - 1);

    /// <summary>The display line showing <paramref name="bufferLine"/> (a folded line maps to its summary).</summary>
    public int ToDisplay(int bufferLine)
    {
        int hidden = 0;
        foreach (TerminalFold fold in _folds)
        {
            if (bufferLine < fold.Start)
            {
                break;
            }

            if (bufferLine < fold.End)
            {
                return fold.Start - hidden;
            }

            hidden += fold.LineCount - 1;
        }

        return bufferLine - hidden;
    }

    /// <summary>The buffer line behind <paramref name="displayLine"/> (a summary maps to its fold's first line).</summary>
    public int ToBuffer(int displayLine) => ToBuffer(displayLine, out _);

    public int ToBuffer(int displayLine, out TerminalFold? summaryOf)
    {
        summaryOf = null;
        int hidden = 0;
        foreach (TerminalFold fold in _folds)
        {
            int summaryDisplay = fold.Start - hidden;
            if (displayLine < summaryDisplay)
            {
                break;
            }

            if (displayLine == summaryDisplay)
            {
                summaryOf = fold;
                return fold.Start;
            }

            hidden += fold.LineCount - 1;
        }

        return displayLine + hidden;
    }

    /// <summary>
    /// The display lines: <paramref name="lines"/> with each fold replaced by
    /// <paramref name="summary"/>(fold). Folds reaching past the end are left unapplied.
    /// </summary>
    public T[] Apply<T>(T[] lines, Func<TerminalFold, T> summary)
    {
        if (_folds.Length == 0)
        {
            return lines;
        }

        var result = new List<T>(lines.Length);
        int next = 0;
        foreach (TerminalFold fold in _folds)
        {
            if (fold.End > lines.Length)
            {
                break;
            }

            for (; next < fold.Start; next++)
            {
                result.Add(lines[next]);
            }

            result.Add(summary(fold));
            next = fold.End;
        }

        for (; next < lines.Length; next++)
        {
            result.Add(lines[next]);
        }

        return result.ToArray();
    }
}
