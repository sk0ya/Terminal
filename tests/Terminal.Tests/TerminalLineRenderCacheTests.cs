using System.Windows.Media;

using Terminal.Buffer;
using Terminal.Rendering;

namespace Terminal.Tests;

public sealed class TerminalLineRenderCacheTests
{
    [Fact]
    public void LayoutAndDrawableAreCreatedLazilyAndReused()
    {
        using var cache = new TerminalLineRenderCache<FakeDrawable>();
        cache.SetSnapshot([Line("first"), Line("second")], ambiguousAsWide: false);
        int builds = 0;

        Assert.Equal(0, cache.CachedCount);
        Assert.Equal("second", cache[1].Text);
        Assert.Equal(1, cache.CachedCount);
        Assert.Equal(0, cache.CachedDrawableCount);

        FakeDrawable first = cache.GetDrawable(1, _ => { builds++; return new FakeDrawable(); });
        FakeDrawable second = cache.GetDrawable(1, _ => { builds++; return new FakeDrawable(); });

        Assert.Same(first, second);
        Assert.Equal(1, builds);
        Assert.Equal(1, cache.CachedDrawableCount);
    }

    [Fact]
    public void InvalidateDisposesDrawableOnceAndKeepsLayout()
    {
        using var cache = CacheWith(Line("text"));
        FakeDrawable old = cache.GetDrawable(0, _ => new FakeDrawable());

        cache.InvalidateDrawables();
        cache.InvalidateDrawables();

        Assert.Equal(1, old.DisposeCount);
        Assert.Equal(1, cache.CachedCount);
        Assert.Equal(0, cache.CachedDrawableCount);
        FakeDrawable replacement = cache.GetDrawable(0, _ => new FakeDrawable());
        Assert.NotSame(old, replacement);
    }

    [Fact]
    public void SnapshotUpdateReusesUnchangedAndEvictsChangedAndRemovedLines()
    {
        using var cache = CacheWith(Line("same"), Line("change"), Line("remove"));
        FakeDrawable same = cache.GetDrawable(0, _ => new FakeDrawable());
        FakeDrawable changed = cache.GetDrawable(1, _ => new FakeDrawable());
        FakeDrawable removed = cache.GetDrawable(2, _ => new FakeDrawable());

        int max = cache.SetSnapshot([Line("same"), Line("new value")], ambiguousAsWide: false);

        Assert.Equal(9, max);
        Assert.Same(same, cache.GetDrawable(0, _ => new FakeDrawable()));
        Assert.Equal(0, same.DisposeCount);
        Assert.Equal(1, changed.DisposeCount);
        Assert.Equal(1, removed.DisposeCount);
        Assert.Equal(1, cache.CachedCount);
    }

    [Fact]
    public void SnapshotUpdateDetectsChangedCachedLineWhenArrayIsReused()
    {
        AnsiTerminalBuffer.TerminalRenderLineSnapshot[] lines = [Line("old")];
        using var cache = new TerminalLineRenderCache<FakeDrawable>();
        cache.SetSnapshot(lines, ambiguousAsWide: false);
        FakeDrawable old = cache.GetDrawable(0, _ => new FakeDrawable());

        lines[0] = Line("new");
        cache.SetSnapshot(lines, ambiguousAsWide: false);

        Assert.Equal(1, old.DisposeCount);
        Assert.Equal("new", cache[0].Text);
    }

    [Fact]
    public void TrimEvictsOnlyEntriesOutsideInclusiveWindow()
    {
        using var cache = CacheWith(Line("0"), Line("1"), Line("2"), Line("3"));
        FakeDrawable[] drawables = Enumerable.Range(0, 4)
            .Select(index => cache.GetDrawable(index, _ => new FakeDrawable()))
            .ToArray();

        cache.TrimOutsideWindow(1, 2);

        Assert.Equal([1, 0, 0, 1], drawables.Select(static item => item.DisposeCount).ToArray());
        Assert.Equal(2, cache.CachedCount);
        Assert.Equal(2, cache.CachedDrawableCount);
    }

    [Fact]
    public void AmbiguousWidthChangeEvictsEverythingAndRebuildsWithNewPolicy()
    {
        AnsiTerminalBuffer.TerminalRenderLineSnapshot line = Line("·X", cellLength: 3);
        using var cache = CacheWith(line);
        TerminalLineLayout narrow = cache[0];
        FakeDrawable drawable = cache.GetDrawable(0, _ => new FakeDrawable());

        cache.SetSnapshot([line], ambiguousAsWide: true);

        Assert.Equal(1, drawable.DisposeCount);
        Assert.Equal(0, cache.CachedCount);
        TerminalLineLayout wide = cache[0];
        Assert.Equal(1, narrow.TextCellMap.GetCellColumn(1, preferTrailingEdge: false));
        Assert.Equal(2, wide.TextCellMap.GetCellColumn(1, preferTrailingEdge: false));
    }

    [Fact]
    public void ClearAllowsReuseAndDisposeIsIdempotent()
    {
        var cache = CacheWith(Line("text"));
        FakeDrawable first = cache.GetDrawable(0, _ => new FakeDrawable());

        cache.Clear();
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal("text", cache[0].Text);
        FakeDrawable second = cache.GetDrawable(0, _ => new FakeDrawable());

        cache.Dispose();
        cache.Dispose();

        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
        Assert.Throws<ObjectDisposedException>(() => cache.GetDrawable(0, _ => new FakeDrawable()));
    }

    [Fact]
    public void InvalidateAttemptsEveryDrawableWhenOneThrowsAndDoesNotDisposeAgain()
    {
        using var cache = CacheWith(Line("0"), Line("1"), Line("2"));
        FakeDrawable[] drawables =
        [
            cache.GetDrawable(0, _ => new FakeDrawable()),
            cache.GetDrawable(1, _ => new FakeDrawable(throwOnDispose: true)),
            cache.GetDrawable(2, _ => new FakeDrawable())
        ];

        Assert.Throws<InvalidOperationException>(() => cache.InvalidateDrawables());

        Assert.Equal([1, 1, 1], drawables.Select(static item => item.DisposeCount).ToArray());
        Assert.Equal(0, cache.CachedDrawableCount);
        cache.InvalidateDrawables();
        Assert.Equal([1, 1, 1], drawables.Select(static item => item.DisposeCount).ToArray());
    }

    [Fact]
    public void ClearCommitsEmptyReusableCacheWhenDrawableDisposeThrows()
    {
        using var cache = CacheWith(Line("0"), Line("1"));
        FakeDrawable first = cache.GetDrawable(0, _ => new FakeDrawable(throwOnDispose: true));
        FakeDrawable second = cache.GetDrawable(1, _ => new FakeDrawable());

        Assert.Throws<InvalidOperationException>(() => cache.Clear());

        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
        Assert.Equal(0, cache.CachedCount);
        Assert.Equal("0", cache[0].Text);
        cache.Clear();
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
    }

    [Fact]
    public void SetSnapshotCommitsNewStateWhenEvictedDrawableDisposeThrows()
    {
        using var cache = CacheWith(Line("old 0"), Line("old 1"));
        FakeDrawable first = cache.GetDrawable(0, _ => new FakeDrawable(throwOnDispose: true));
        FakeDrawable second = cache.GetDrawable(1, _ => new FakeDrawable());

        Assert.Throws<InvalidOperationException>(() =>
            cache.SetSnapshot([Line("new")], ambiguousAsWide: false));

        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
        Assert.Equal(1, cache.Count);
        Assert.Equal(0, cache.CachedCount);
        Assert.Equal("new", cache[0].Text);
    }

    [Fact]
    public void TrimCommitsEvictionWhenDrawableDisposeThrows()
    {
        using var cache = CacheWith(Line("0"), Line("1"), Line("2"));
        FakeDrawable first = cache.GetDrawable(0, _ => new FakeDrawable(throwOnDispose: true));
        FakeDrawable middle = cache.GetDrawable(1, _ => new FakeDrawable());
        FakeDrawable last = cache.GetDrawable(2, _ => new FakeDrawable());

        Assert.Throws<InvalidOperationException>(() => cache.TrimOutsideWindow(1, 1));

        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(0, middle.DisposeCount);
        Assert.Equal(1, last.DisposeCount);
        Assert.Equal(1, cache.CachedCount);
        Assert.Equal(1, cache.CachedDrawableCount);
    }

    [Fact]
    public void DisposeCommitsDisposedStateAndAttemptsEveryDrawableWhenOneThrows()
    {
        var cache = CacheWith(Line("0"), Line("1"));
        FakeDrawable first = cache.GetDrawable(0, _ => new FakeDrawable(throwOnDispose: true));
        FakeDrawable second = cache.GetDrawable(1, _ => new FakeDrawable());

        Assert.Throws<InvalidOperationException>(() => cache.Dispose());

        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
        cache.Dispose();
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
        Assert.Throws<ObjectDisposedException>(() => cache.SetSnapshot([], ambiguousAsWide: false));
    }

    private static TerminalLineRenderCache<FakeDrawable> CacheWith(
        params AnsiTerminalBuffer.TerminalRenderLineSnapshot[] lines)
    {
        var cache = new TerminalLineRenderCache<FakeDrawable>();
        cache.SetSnapshot(lines, ambiguousAsWide: false);
        return cache;
    }

    private static AnsiTerminalBuffer.TerminalRenderLineSnapshot Line(string text, int? cellLength = null) =>
        new(cellLength ?? text.Length, [new AnsiTerminalBuffer.TerminalRenderSegmentSnapshot(
            text, cellLength ?? text.Length, Colors.White, Colors.Black, false, false, UnderlineStyle.None,
            null, false, false, null, false)]);

    private sealed class FakeDrawable(bool throwOnDispose = false) : IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
            if (throwOnDispose)
            {
                throw new InvalidOperationException("dispose failed");
            }
        }
    }
}
