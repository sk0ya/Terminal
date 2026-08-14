using Terminal.Buffer;

namespace Terminal.Rendering;

/// <summary>
/// Lazily owns line layouts and their disposable, shaped drawing resources for one render snapshot.
/// </summary>
/// <remarks>
/// The cache is the sole owner of every drawable returned by <see cref="GetDrawable"/>. A drawable
/// is disposed exactly once when invalidated, evicted, cleared, or when this cache is disposed.
/// </remarks>
internal sealed class TerminalLineRenderCache<TDrawable> : IDisposable
    where TDrawable : class, IDisposable
{
    private sealed class Entry(
        AnsiTerminalBuffer.TerminalRenderLineSnapshot snapshot,
        TerminalLineLayout layout)
    {
        public AnsiTerminalBuffer.TerminalRenderLineSnapshot Snapshot { get; } = snapshot;
        public TerminalLineLayout Layout { get; } = layout;
        public TDrawable? Drawable { get; set; }
    }

    private readonly Dictionary<int, Entry> _cache = [];
    private readonly List<int> _evictionScratch = [];
    private AnsiTerminalBuffer.TerminalRenderLineSnapshot[] _snapshot = [];
    private bool _ambiguousAsWide;
    private bool _disposed;

    public int Count => _snapshot.Length;

    public int CachedCount => _cache.Count;

    public int CachedDrawableCount => _cache.Values.Count(static entry => entry.Drawable is not null);

    public TerminalLineLayout this[int index] => GetEntry(index).Layout;

    /// <summary>
    /// Returns the value snapshot for a line without materializing its layout, for callers that
    /// only need the lightweight per-line data.
    /// </summary>
    public AnsiTerminalBuffer.TerminalRenderLineSnapshot GetSnapshot(int index) => _snapshot[index];

    public TDrawable GetDrawable(int index, Func<TerminalLineLayout, TDrawable> builder)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(builder);

        Entry entry = GetEntry(index);
        return entry.Drawable ??= builder(entry.Layout);
    }

    public void InvalidateDrawables()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        DisposableResourceOwner.DisposeAllBestEffort(DetachDrawables(_cache.Values));
    }

    public int SetSnapshot(AnsiTerminalBuffer.TerminalRenderLineSnapshot[] lines, bool ambiguousAsWide)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(lines);

        bool reuse = ambiguousAsWide == _ambiguousAsWide && _cache.Count > 0;
        int maxCellLength = lines.Length == 0 ? 0 : lines.Max(static line => line.CellLength);

        _snapshot = lines;
        _ambiguousAsWide = ambiguousAsWide;

        if (!reuse)
        {
            ClearEntries();
            return maxCellLength;
        }

        _evictionScratch.Clear();
        foreach (int index in _cache.Keys)
        {
            if (index >= lines.Length || !lines[index].ContentEquals(_cache[index].Snapshot))
            {
                _evictionScratch.Add(index);
            }
        }

        EvictMarkedEntries();
        return maxCellLength;
    }

    public void TrimOutsideWindow(int startInclusive, int endInclusive)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _evictionScratch.Clear();
        foreach (int index in _cache.Keys)
        {
            if (index < startInclusive || index > endInclusive)
            {
                _evictionScratch.Add(index);
            }
        }

        EvictMarkedEntries();
    }

    /// <summary>
    /// Releases materialized layouts and drawables while retaining the snapshot for future reload/render.
    /// </summary>
    public void Clear()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ClearEntries();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        List<TDrawable> drawables = DetachDrawables(_cache.Values);
        _cache.Clear();
        _evictionScratch.Clear();
        _snapshot = [];
        _disposed = true;
        DisposableResourceOwner.DisposeAllBestEffort(drawables);
    }

    private Entry GetEntry(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_cache.TryGetValue(index, out Entry? entry))
        {
            AnsiTerminalBuffer.TerminalRenderLineSnapshot snapshot = _snapshot[index];
            entry = new Entry(snapshot, TerminalLineLayoutBuilder.Create(snapshot, _ambiguousAsWide));
            _cache[index] = entry;
        }

        return entry;
    }

    private void EvictMarkedEntries()
    {
        var evicted = new List<TDrawable>();
        foreach (int index in _evictionScratch)
        {
            if (_cache.Remove(index, out Entry? entry))
            {
                TDrawable? drawable = entry.Drawable;
                entry.Drawable = null;
                if (drawable is not null)
                {
                    evicted.Add(drawable);
                }
            }
        }

        _evictionScratch.Clear();
        DisposableResourceOwner.DisposeAllBestEffort(evicted);
    }

    private void ClearEntries()
    {
        List<TDrawable> drawables = DetachDrawables(_cache.Values);
        _cache.Clear();
        _evictionScratch.Clear();
        DisposableResourceOwner.DisposeAllBestEffort(drawables);
    }

    private static List<TDrawable> DetachDrawables(IEnumerable<Entry> entries)
    {
        var drawables = new List<TDrawable>();
        foreach (Entry entry in entries)
        {
            TDrawable? drawable = entry.Drawable;
            entry.Drawable = null;
            if (drawable is not null)
            {
                drawables.Add(drawable);
            }
        }

        return drawables;
    }
}
