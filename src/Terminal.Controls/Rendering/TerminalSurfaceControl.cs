using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using System.Windows.Threading;

using Terminal.Buffer;
using Terminal.Tabs;
using Terminal.Unicode;

namespace Terminal.Rendering;

public sealed class TerminalSurfaceControl : Control, IScrollInfo
{
    private static readonly Color DefaultBackgroundColor = Color.FromRgb(0x0E, 0x0C, 0x0A);
    private static readonly Color DefaultForegroundColor = Color.FromRgb(0xE8, 0xE0, 0xD2);
    private static readonly Brush DefaultSelectionBrush = CreateFrozenBrush(Color.FromArgb(0x66, 0xE1, 0x9A, 0x4A));
    private static readonly Brush DefaultBackgroundBrush = CreateFrozenBrush(DefaultBackgroundColor);
    private static readonly Brush DefaultForegroundBrush = CreateFrozenBrush(DefaultForegroundColor);

    private readonly Dictionary<Color, SolidColorBrush> _brushCache = [];
    // Virtualized line layouts: heavy LineLayout objects (grapheme maps, segment arrays) are built
    // lazily for the lines that are actually touched (the visible window plus any selection/search
    // probes) and evicted once they scroll out, so memory and per-update CPU scale with the viewport
    // rather than with the full scrollback history.
    private readonly VirtualLineLayouts _lines = new();
    private bool _ambiguousAsWide;
    private Typeface? _typeface;
    private Typeface? _italicTypeface;
    private GlyphTypeface? _primaryGlyphTypeface;
    private FontFallbackResolver? _fontFallback;
    private bool _fontLigaturesEnabled;
    private TextFormatter? _textFormatter;
    private Size _cellSize = new(8, 16);
    private double _pixelsPerDip = 1.0;
    private bool _metricsDirty = true;
    private int _maxCellLength;
    private TerminalTextRange? _selection;
    private TerminalTextPosition? _selectionAnchor;
    private Point? _selectionAnchorPoint;
    private bool _selectionDragStarted;
    private bool _blockSelectionMode;
    // The link (URL/file path) currently under the mouse pointer, drawn with an underline so it
    // reads as clickable. Cell-column span on a single line; null when not hovering a link.
    private (int Line, int StartColumn, int EndColumn)? _hoveredLink;
    private double _blockAnchorCellColumn;
    private double _blockCurrentCellColumn;
    private TerminalTextPosition _keyboardCursor;
    private TerminalTextPosition? _keyboardAnchor;
    private double _extentWidth;
    private double _extentHeight;
    private double _lastReportedExtentWidth;
    private double _lastReportedExtentHeight;
    private double _viewportWidth;
    private double _viewportHeight;
    private double _viewportFloorWidth;
    private double _viewportFloorHeight;
    private double _horizontalOffset;
    private double _verticalOffset;
    // SGR 5/6 text blink: a timer toggles _blinkTextVisible on a fixed cadence and OnRender skips
    // blinking runs on the off phase. The timer only runs while blinking runs are actually on screen
    // (decided each paint), so a terminal with no blinking content never repaints for blink.
    private static readonly TimeSpan BlinkInterval = TimeSpan.FromMilliseconds(500);
    private readonly DispatcherTimer _blinkTimer;
    private bool _blinkTextVisible = true;

    public event EventHandler<TerminalHyperlinkActivatedEventArgs>? HyperlinkActivated;

    static TerminalSurfaceControl()
    {
        FocusableProperty.OverrideMetadata(typeof(TerminalSurfaceControl), new FrameworkPropertyMetadata(true));
    }

    public TerminalSurfaceControl()
    {
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        FocusVisualStyle = null;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        RequestBringIntoView += OnRequestBringIntoView;
        _blinkTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = BlinkInterval };
        _blinkTimer.Tick += OnBlinkTimerTick;
        Unloaded += (_, _) => StopBlinkTimer();
    }

    private void OnBlinkTimerTick(object? sender, EventArgs e)
    {
        _blinkTextVisible = !_blinkTextVisible;
        InvalidateVisual();
    }

    // Called at the end of each paint with whether any blinking run is currently on screen. Starts
    // the cadence when blink content appears and stops it (restoring visibility) when it is gone, so
    // the timer never runs for a terminal that has no blinking text in view.
    private void UpdateBlinkTimer(bool hasBlinkingContent)
    {
        if (hasBlinkingContent)
        {
            if (!_blinkTimer.IsEnabled)
            {
                _blinkTimer.Start();
            }
        }
        else
        {
            StopBlinkTimer();
        }
    }

    private void StopBlinkTimer()
    {
        if (!_blinkTimer.IsEnabled)
        {
            return;
        }

        _blinkTimer.Stop();
        if (!_blinkTextVisible)
        {
            // Leave the surface showing the text rather than frozen on a blink-off frame.
            _blinkTextVisible = true;
            InvalidateVisual();
        }
    }

    private static void OnRequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        // Clicking the surface focuses it, and WPF then raises RequestBringIntoView, which the
        // hosting ScrollViewer services via IScrollInfo.MakeVisible — scrolling the viewport
        // (typically snapping to the top) even though the user did not scroll. The terminal owns
        // its scroll position: follow-output uses ScrollToVerticalOffset and keyboard-cursor
        // navigation calls MakeVisible directly, neither of which routes through this event.
        // Suppress the focus-driven bring-into-view so a click never moves the viewport.
        if (ReferenceEquals(e.TargetObject, sender))
        {
            e.Handled = true;
        }
    }

    public bool CanHorizontallyScroll { get; set; } = true;

    public bool CanVerticallyScroll { get; set; } = true;

    public double ExtentWidth => _extentWidth;

    public double ExtentHeight => _extentHeight;

    public double ViewportWidth => _viewportWidth;

    public double ViewportHeight => _viewportHeight;

    public double HorizontalOffset => _horizontalOffset;

    public double VerticalOffset => _verticalOffset;

    public ScrollViewer? ScrollOwner { get; set; }

    public bool HasSelection => _selection.HasValue && !_selection.Value.IsEmpty;

    public Brush? SelectionBackground
    {
        get => (Brush?)GetValue(SelectionBackgroundProperty);
        set => SetValue(SelectionBackgroundProperty, value);
    }

    public static readonly DependencyProperty SelectionBackgroundProperty =
        DependencyProperty.Register(
            nameof(SelectionBackground),
            typeof(Brush),
            typeof(TerminalSurfaceControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public int LineCount => _lines.Count;

    // Test hook: number of LineLayout objects currently materialized. Virtualization keeps this
    // bounded to roughly the viewport regardless of total scrollback size.
    internal int CachedLineLayoutCount => _lines.CachedCount;

    // Test hook: number of lines whose shaped, ready-to-paint drawable is currently cached. Each
    // line shapes its glyphs once and reuses them across repaints until the line content or font
    // metrics change.
    internal int CachedLineDrawableCount => _lines.CachedDrawableCount;

    // When enabled, primary-font runs are shaped through TextFormatter with OpenType standard
    // ligatures and contextual alternates turned on, so programming fonts (FiraCode, Cascadia Code)
    // render sequences like "=>", "!=", "->" as their ligated glyphs. Default off so the exact
    // one-cell-per-glyph rendering is preserved. Wide/fallback runs keep cell-by-cell positioning.
    public bool FontLigaturesEnabled
    {
        get => _fontLigaturesEnabled;
        set
        {
            if (_fontLigaturesEnabled == value)
            {
                return;
            }

            _fontLigaturesEnabled = value;
            _lines.InvalidateDrawables();
            InvalidateVisual();
        }
    }

    public Size CharacterCellSize
    {
        get
        {
            EnsureMetrics();
            return _cellSize;
        }
    }

    internal void UpdateSnapshot(AnsiTerminalBuffer.TerminalRenderSnapshot snapshot)
    {
        EnsureMetrics();
        // The maximum cell length (for the horizontal scroll extent) comes straight from the
        // lightweight value snapshots, so no LineLayout has to be built here. Layouts are produced
        // on demand by the cache when OnRender / selection / search ask for a specific line.
        _ambiguousAsWide = snapshot.AmbiguousWidthIsWide;
        _maxCellLength = _lines.SetSnapshot(snapshot.Lines, snapshot.AmbiguousWidthIsWide);

        CoerceSelection();
        CoerceKeyboardCursor();
        UpdateScrollMetrics();
        InvalidateVisual();
    }

    internal void SetViewportFloor(Size size)
    {
        double nextWidth = NormalizeViewportFloorExtent(size.Width);
        double nextHeight = NormalizeViewportFloorExtent(size.Height);
        if (DoubleUtil.AreClose(_viewportFloorWidth, nextWidth) &&
            DoubleUtil.AreClose(_viewportFloorHeight, nextHeight))
        {
            return;
        }

        _viewportFloorWidth = nextWidth;
        _viewportFloorHeight = nextHeight;
        UpdateScrollMetrics();
        InvalidateMeasure();
        InvalidateVisual();
    }

    public void ClearSelection()
    {
        if (!_selection.HasValue)
        {
            return;
        }

        _selection = null;
        _keyboardAnchor = null;
        _blockSelectionMode = false;
        InvalidateVisual();
    }

    public bool MoveKeyboardCursor(Key key, bool extend)
    {
        if (_lines.Count == 0)
        {
            return false;
        }

        if (!extend)
        {
            _keyboardAnchor = null;
            _selection = null;
            _keyboardCursor = AdvanceKeyboardCursor(_keyboardCursor, key);
            InvalidateVisual();
            return true;
        }

        if (!_keyboardAnchor.HasValue)
        {
            TerminalTextPosition start = _selection.HasValue
                ? NormalizeSelection(_selection)!.Value.Start
                : _keyboardCursor;
            _keyboardAnchor = start;
            _keyboardCursor = _selection.HasValue ? NormalizeSelection(_selection)!.Value.End : start;
        }

        _keyboardCursor = AdvanceKeyboardCursor(_keyboardCursor, key);
        SelectRange(new TerminalTextRange(_keyboardAnchor.Value, _keyboardCursor));
        return true;
    }

    private TerminalTextPosition AdvanceKeyboardCursor(TerminalTextPosition pos, Key key)
    {
        switch (key)
        {
            case Key.Right:
            {
                int lineIndex = Math.Clamp(pos.LineIndex, 0, _lines.Count - 1);
                LineLayout line = _lines[lineIndex];
                if (pos.TextIndex < line.Text.Length)
                    return new TerminalTextPosition(lineIndex, pos.TextIndex + 1);
                if (lineIndex < _lines.Count - 1)
                    return new TerminalTextPosition(lineIndex + 1, 0);
                return pos;
            }
            case Key.Left:
            {
                int lineIndex = Math.Clamp(pos.LineIndex, 0, _lines.Count - 1);
                int textIndex = Math.Clamp(pos.TextIndex, 0, _lines[lineIndex].Text.Length);
                if (textIndex > 0)
                    return new TerminalTextPosition(lineIndex, textIndex - 1);
                if (lineIndex > 0)
                {
                    int prev = lineIndex - 1;
                    return new TerminalTextPosition(prev, _lines[prev].Text.Length);
                }
                return new TerminalTextPosition(lineIndex, textIndex);
            }
            case Key.Down:
            {
                int lineIndex = Math.Clamp(pos.LineIndex, 0, _lines.Count - 1);
                if (lineIndex < _lines.Count - 1)
                {
                    int next = lineIndex + 1;
                    return new TerminalTextPosition(next, Math.Min(pos.TextIndex, _lines[next].Text.Length));
                }
                return new TerminalTextPosition(lineIndex, Math.Min(pos.TextIndex, _lines[lineIndex].Text.Length));
            }
            case Key.Up:
            {
                int lineIndex = Math.Clamp(pos.LineIndex, 0, _lines.Count - 1);
                if (lineIndex > 0)
                {
                    int prev = lineIndex - 1;
                    return new TerminalTextPosition(prev, Math.Min(pos.TextIndex, _lines[prev].Text.Length));
                }
                return new TerminalTextPosition(0, Math.Min(pos.TextIndex, _lines[0].Text.Length));
            }
            default:
                return pos;
        }
    }

    public string GetSelectedText()
    {
        TerminalTextRange? selection = NormalizeSelection(_selection);
        if (!selection.HasValue)
        {
            return string.Empty;
        }

        TerminalTextRange range = selection.Value;

        if (_blockSelectionMode)
        {
            return GetBlockSelectedText(range);
        }

        var builder = new StringBuilder();
        for (int lineIndex = range.Start.LineIndex; lineIndex <= range.End.LineIndex; lineIndex++)
        {
            LineLayout line = _lines[lineIndex];
            int start = lineIndex == range.Start.LineIndex ? range.Start.TextIndex : 0;
            int end = lineIndex == range.End.LineIndex ? range.End.TextIndex : line.Text.Length;
            start = Math.Clamp(start, 0, line.Text.Length);
            end = Math.Clamp(end, start, line.Text.Length);
            builder.Append(line.Text.AsSpan(start, end - start));
            if (lineIndex < range.End.LineIndex)
            {
                builder.AppendLine();
            }
        }

        return builder.ToString();
    }

    private (int Left, int Right) GetBlockColumnRange()
    {
        int left = (int)Math.Min(_blockAnchorCellColumn, _blockCurrentCellColumn);
        int right = (int)Math.Ceiling(Math.Max(_blockAnchorCellColumn, _blockCurrentCellColumn));
        if (right <= left)
        {
            right = left + 1;
        }

        return (left, right);
    }

    private string GetBlockSelectedText(TerminalTextRange range)
    {
        var (leftColumn, rightColumn) = GetBlockColumnRange();
        var builder = new StringBuilder();
        for (int lineIndex = range.Start.LineIndex; lineIndex <= range.End.LineIndex; lineIndex++)
        {
            LineLayout line = _lines[lineIndex];
            int startTextIndex = line.TextCellMap.GetTextIndex(leftColumn);
            int endTextIndex = line.TextCellMap.GetTextIndex(rightColumn);
            startTextIndex = Math.Clamp(startTextIndex, 0, line.Text.Length);
            endTextIndex = Math.Clamp(endTextIndex, startTextIndex, line.Text.Length);
            builder.Append(line.Text.AsSpan(startTextIndex, endTextIndex - startTextIndex));
            if (lineIndex < range.End.LineIndex)
            {
                builder.AppendLine();
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// 現在の選択範囲を「行ごとの装飾付きラン列」（各セルの表示文字＋解決済み前景/背景 RGB＋
    /// bold/italic/underline）として取り出す。色付きコピー（HTML/RTF）用の中間表現で、通常選択・
    /// 矩形選択の双方に対応する。選択が無ければ <c>null</c> を返す。
    /// </summary>
    internal StyledSelection? GetStyledSelection()
    {
        TerminalTextRange? selection = NormalizeSelection(_selection);
        if (!selection.HasValue)
        {
            return null;
        }

        TerminalTextRange range = selection.Value;
        (int Left, int Right) blockColumns = _blockSelectionMode ? GetBlockColumnRange() : default;

        var lines = new List<IReadOnlyList<StyledRun>>();
        for (int lineIndex = range.Start.LineIndex; lineIndex <= range.End.LineIndex; lineIndex++)
        {
            LineLayout line = _lines[lineIndex];
            int start;
            int end;
            if (_blockSelectionMode)
            {
                start = line.TextCellMap.GetTextIndex(blockColumns.Left);
                end = line.TextCellMap.GetTextIndex(blockColumns.Right);
            }
            else
            {
                start = lineIndex == range.Start.LineIndex ? range.Start.TextIndex : 0;
                end = lineIndex == range.End.LineIndex ? range.End.TextIndex : line.Text.Length;
            }

            start = Math.Clamp(start, 0, line.Text.Length);
            end = Math.Clamp(end, start, line.Text.Length);
            lines.Add(BuildStyledRuns(line, start, end));
        }

        Color background = (Background as SolidColorBrush)?.Color ?? DefaultBackgroundColor;
        Color foreground = (Foreground as SolidColorBrush)?.Color ?? DefaultForegroundColor;
        return new StyledSelection(lines, foreground, background);
    }

    // 1 行の [startTextIndex, endTextIndex) をセグメント境界で切り出し、各セグメントの解決済み
    // スタイルを持つ装飾付きランへ変換する。line.Text は各セグメント Text の連結なので、文字
    // オフセットを累積してテキストインデックス範囲と交差させる。
    private static List<StyledRun> BuildStyledRuns(LineLayout line, int startTextIndex, int endTextIndex)
    {
        var runs = new List<StyledRun>();
        if (endTextIndex <= startTextIndex)
        {
            return runs;
        }

        int charOffset = 0;
        foreach (SegmentLayout segment in line.Segments)
        {
            AnsiTerminalBuffer.TerminalRenderSegmentSnapshot snapshot = segment.Snapshot;
            int segStart = charOffset;
            int segEnd = charOffset + snapshot.Text.Length;
            charOffset = segEnd;

            int overlapStart = Math.Max(startTextIndex, segStart);
            int overlapEnd = Math.Min(endTextIndex, segEnd);
            if (overlapEnd <= overlapStart)
            {
                continue;
            }

            string text = line.Text.Substring(overlapStart, overlapEnd - overlapStart);
            runs.Add(new StyledRun(
                text,
                snapshot.Foreground,
                snapshot.Background,
                snapshot.Bold,
                snapshot.Italic,
                snapshot.UnderlineStyle != UnderlineStyle.None));
        }

        return runs;
    }

    public int CountMatches(string query, StringComparison comparison)
    {
        if (string.IsNullOrEmpty(query))
        {
            return 0;
        }

        int count = 0;
        for (int lineIndex = 0; lineIndex < _lines.Count; lineIndex++)
        {
            LineLayout line = _lines[lineIndex];
            int index = 0;
            while (index < line.Text.Length)
            {
                int found = line.Text.IndexOf(query, index, comparison);
                if (found < 0)
                {
                    break;
                }

                count++;
                index = found + query.Length;
            }
        }

        return count;
    }

    /// <summary>
    /// バッファ全体から <paramref name="query"/> の一致をすべて列挙する（行頭→行末、上から下へ）。
    /// 同一行に複数あれば重なりなく順に返す。選択状態やスクロール位置は変更しない。
    /// </summary>
    public IReadOnlyList<TerminalMatch> FindMatches(string query, StringComparison comparison)
    {
        var matches = new List<TerminalMatch>();
        if (string.IsNullOrEmpty(query))
        {
            return matches;
        }

        for (int lineIndex = 0; lineIndex < _lines.Count; lineIndex++)
        {
            LineLayout line = _lines[lineIndex];
            int index = 0;
            while (index <= line.Text.Length)
            {
                int found = line.Text.IndexOf(query, index, comparison);
                if (found < 0)
                {
                    break;
                }

                matches.Add(new TerminalMatch(lineIndex, found, query.Length, line.Text));
                index = found + query.Length;
            }
        }

        return matches;
    }

    /// <summary>
    /// 指定位置の範囲を選択ハイライトし、その箇所までスクロールして可視化する
    /// （<see cref="FindMatches"/> で得た一致へジャンプする用途）。範囲は行内にクランプする。
    /// </summary>
    /// <returns>行インデックスが有効で選択できれば <c>true</c>。</returns>
    public bool SelectMatch(int lineIndex, int column, int length)
    {
        if (lineIndex < 0 || lineIndex >= _lines.Count)
        {
            return false;
        }

        LineLayout line = _lines[lineIndex];
        int start = Math.Clamp(column, 0, line.Text.Length);
        int end = Math.Clamp(column + length, start, line.Text.Length);
        SelectRange(new TerminalTextRange(
            new TerminalTextPosition(lineIndex, start),
            new TerminalTextPosition(lineIndex, end)));
        return true;
    }

    public bool TrySelectNextMatch(string query, StringComparison comparison, bool forward, out bool wrapped)
    {
        wrapped = false;
        if (string.IsNullOrEmpty(query) || _lines.Count == 0)
        {
            return false;
        }

        if (forward)
        {
            TerminalTextPosition start = _selection.HasValue
                ? NormalizeSelection(_selection)!.Value.End
                : new TerminalTextPosition(0, 0);
            if (TryFindForward(start, query, comparison, out TerminalTextRange match))
            {
                SelectRange(match);
                return true;
            }

            wrapped = TryFindForward(new TerminalTextPosition(0, 0), query, comparison, out match);
            if (wrapped)
            {
                SelectRange(match);
            }

            return wrapped;
        }

        TerminalTextPosition backwardStart = _selection.HasValue
            ? NormalizeSelection(_selection)!.Value.Start
            : new TerminalTextPosition(_lines.Count - 1, _lines[^1].Text.Length);
        if (TryFindBackward(backwardStart, query, comparison, out TerminalTextRange backwardMatch))
        {
            SelectRange(backwardMatch);
            return true;
        }

        wrapped = TryFindBackward(
            new TerminalTextPosition(_lines.Count - 1, _lines[^1].Text.Length),
            query,
            comparison,
            out backwardMatch);
        if (wrapped)
        {
            SelectRange(backwardMatch);
        }

        return wrapped;
    }

    public Rect GetCellRect(int lineIndex, int column)
    {
        EnsureMetrics();
        Thickness padding = Padding;
        return new Rect(
            padding.Left + (Math.Max(0, column) * _cellSize.Width),
            padding.Top + (Math.Max(0, lineIndex) * _cellSize.Height),
            _cellSize.Width,
            _cellSize.Height);
    }

    private double GetCellColumnFromPoint(Point point)
    {
        return CreateCoordinateMapper().GetCellColumn(point.X);
    }

    public bool TryGetTextPositionFromPoint(Point point, out int lineIndex, out int textIndex)
    {
        EnsureMetrics();
        if (_lines.Count == 0)
        {
            lineIndex = 0;
            textIndex = 0;
            return false;
        }

        TerminalSurfaceCoordinateMapper coordinates = CreateCoordinateMapper();
        lineIndex = coordinates.GetLineIndex(point.Y, _lines.Count);

        LineLayout line = _lines[lineIndex];
        textIndex = line.TextCellMap.GetTextIndex(coordinates.GetCellColumn(point.X));
        return true;
    }

    private TerminalSurfaceCoordinateMapper CreateCoordinateMapper()
    {
        Thickness padding = Padding;
        return new TerminalSurfaceCoordinateMapper(
            _cellSize.Width,
            _cellSize.Height,
            padding.Left,
            padding.Top,
            _horizontalOffset,
            _verticalOffset);
    }

    public void ScrollToLineEnd()
    {
        SetVerticalOffset(Math.Max(0, ExtentHeight - ViewportHeight));
    }

    public void LineUp()
    {
        SetVerticalOffset(_verticalOffset - CharacterCellSize.Height);
    }

    public void LineDown()
    {
        SetVerticalOffset(_verticalOffset + CharacterCellSize.Height);
    }

    public void LineLeft()
    {
        SetHorizontalOffset(_horizontalOffset - CharacterCellSize.Width);
    }

    public void LineRight()
    {
        SetHorizontalOffset(_horizontalOffset + CharacterCellSize.Width);
    }

    public void PageUp()
    {
        SetVerticalOffset(_verticalOffset - _viewportHeight);
    }

    public void PageDown()
    {
        SetVerticalOffset(_verticalOffset + _viewportHeight);
    }

    public void PageLeft()
    {
        SetHorizontalOffset(_horizontalOffset - _viewportWidth);
    }

    public void PageRight()
    {
        SetHorizontalOffset(_horizontalOffset + _viewportWidth);
    }

    public void MouseWheelUp()
    {
        SetVerticalOffset(_verticalOffset - SystemParameters.WheelScrollLines * CharacterCellSize.Height);
    }

    public void MouseWheelDown()
    {
        SetVerticalOffset(_verticalOffset + SystemParameters.WheelScrollLines * CharacterCellSize.Height);
    }

    public void MouseWheelLeft()
    {
        SetHorizontalOffset(_horizontalOffset - (SystemParameters.WheelScrollLines * CharacterCellSize.Width));
    }

    public void MouseWheelRight()
    {
        SetHorizontalOffset(_horizontalOffset + (SystemParameters.WheelScrollLines * CharacterCellSize.Width));
    }

    public void SetHorizontalOffset(double offset)
    {
        double next = Math.Clamp(offset, 0, Math.Max(0, ExtentWidth - ViewportWidth));
        if (DoubleUtil.AreClose(_horizontalOffset, next))
        {
            return;
        }

        _horizontalOffset = next;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateVisual();
    }

    public void SetVerticalOffset(double offset)
    {
        double next = Math.Clamp(offset, 0, Math.Max(0, ExtentHeight - ViewportHeight));
        if (DoubleUtil.AreClose(_verticalOffset, next))
        {
            return;
        }

        _verticalOffset = next;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateVisual();
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        if (!ReferenceEquals(visual, this))
        {
            return rectangle;
        }

        if (rectangle.Left < _horizontalOffset)
        {
            SetHorizontalOffset(rectangle.Left);
        }
        else if (rectangle.Right > _horizontalOffset + _viewportWidth)
        {
            SetHorizontalOffset(rectangle.Right - _viewportWidth);
        }

        if (rectangle.Top < _verticalOffset)
        {
            SetVerticalOffset(rectangle.Top);
        }
        else if (rectangle.Bottom > _verticalOffset + _viewportHeight)
        {
            SetVerticalOffset(rectangle.Bottom - _viewportHeight);
        }

        rectangle.Intersect(new Rect(_horizontalOffset, _verticalOffset, _viewportWidth, _viewportHeight));
        return rectangle;
    }

    protected override Size MeasureOverride(Size constraint)
    {
        EnsureMetrics();
        Size viewportSize = ResolveViewportSize(constraint);
        UpdateViewport(viewportSize);
        return new Size(
            double.IsInfinity(constraint.Width) ? ExtentWidth : Math.Max(constraint.Width, _viewportFloorWidth),
            double.IsInfinity(constraint.Height) ? ExtentHeight : Math.Max(constraint.Height, _viewportFloorHeight));
    }

    protected override Size ArrangeOverride(Size arrangeBounds)
    {
        EnsureMetrics();
        UpdateViewport(arrangeBounds);
        return arrangeBounds;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        EnsureMetrics();
        Thickness padding = Padding;
        Brush background = Background ?? DefaultBackgroundBrush;
        drawingContext.DrawRectangle(background, null, new Rect(new Point(0, 0), RenderSize));

        if (_lines.Count == 0 || _cellSize.Width <= 0 || _cellSize.Height <= 0)
        {
            // Nothing is painted, so no blinking run can be on screen — make sure the cadence isn't
            // left running from a previous frame that did have blink content.
            StopBlinkTimer();
            return;
        }

        double contentLeft = padding.Left - _horizontalOffset;
        double contentTop = padding.Top - _verticalOffset;
        int firstVisibleLine = Math.Max(0, (int)Math.Floor(Math.Max(0, _verticalOffset - padding.Top) / _cellSize.Height));
        int lastVisibleLine = Math.Min(
            _lines.Count - 1,
            (int)Math.Ceiling(Math.Max(0, (_verticalOffset - padding.Top) + _viewportHeight) / _cellSize.Height));

        // Drop layouts that have scrolled out of view (plus a one-screen margin on each side so
        // small scrolls reuse them) to keep the cache bounded to roughly the viewport.
        int visibleSpan = lastVisibleLine - firstVisibleLine + 1;
        _lines.TrimOutsideWindow(firstVisibleLine - visibleSpan, lastVisibleLine + visibleSpan);

        TerminalTextRange? selection = NormalizeSelection(_selection);
        bool sawBlinkingContent = false;
        for (int lineIndex = firstVisibleLine; lineIndex <= lastVisibleLine; lineIndex++)
        {
            LineLayout line = _lines[lineIndex];
            double top = contentTop + (lineIndex * _cellSize.Height);
            DrawLineBackgrounds(drawingContext, line, top, contentLeft);
            if (_blockSelectionMode && selection.HasValue)
            {
                DrawBlockSelection(drawingContext, selection.Value, lineIndex, top, contentLeft);
            }
            else
            {
                DrawSelection(drawingContext, selection, lineIndex, line, top, contentLeft);
            }

            LineDrawable drawable = _lines.GetDrawable(lineIndex, BuildLineDrawable);
            foreach (IDrawCommand command in drawable.Commands)
            {
                if (command.Blink)
                {
                    sawBlinkingContent = true;
                    if (!_blinkTextVisible)
                    {
                        continue;
                    }
                }

                command.Render(drawingContext, contentLeft, top);
            }

            if (_hoveredLink is { } hovered && hovered.Line == lineIndex)
            {
                DrawHoverUnderline(drawingContext, hovered.StartColumn, hovered.EndColumn, top, contentLeft);
            }
        }

        UpdateBlinkTimer(sawBlinkingContent);
    }

    private void DrawHoverUnderline(DrawingContext drawingContext, int startColumn, int endColumn, double top, double contentLeft)
    {
        if (endColumn <= startColumn)
        {
            return;
        }

        double x1 = contentLeft + (startColumn * _cellSize.Width);
        double x2 = contentLeft + (endColumn * _cellSize.Width);
        double y = Math.Round(top + _cellSize.Height - 1.0) + 0.5; // crisp 1px line on the cell baseline
        var pen = new Pen(Foreground ?? DefaultForegroundBrush, 1.0);
        pen.Freeze();
        drawingContext.DrawLine(pen, new Point(x1, y), new Point(x2, y));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        Focus();
        if (!TryCreateTextPosition(e.GetPosition(this), out TerminalTextPosition position))
        {
            return;
        }

        // Ctrl+Click activates the link (URL/file path) under the pointer. Done on mouse-down so it
        // isn't lost to the capture release in TerminalTabView's PreviewMouseUp, and so it doesn't
        // interfere with plain-click/drag text selection.
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 &&
            TryGetHyperlink(position.LineIndex, position.TextIndex, out string? linkTarget) &&
            !string.IsNullOrEmpty(linkTarget))
        {
            HyperlinkActivated?.Invoke(this, new TerminalHyperlinkActivatedEventArgs(linkTarget));
            e.Handled = true;
            return;
        }

        if (e.ClickCount >= 3)
        {
            _blockSelectionMode = false;
            SelectLine(position.LineIndex);
            _selectionAnchor = position;
            _selectionAnchorPoint = e.GetPosition(this);
            _selectionDragStarted = false;
            CaptureMouse();
            e.Handled = true;
            return;
        }

        if (e.ClickCount == 2)
        {
            _blockSelectionMode = false;
            SelectWord(position.LineIndex, position.TextIndex);
            _selectionAnchor = position;
            _selectionAnchorPoint = e.GetPosition(this);
            _selectionDragStarted = false;
            CaptureMouse();
            e.Handled = true;
            return;
        }

        bool altDown = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        _blockSelectionMode = altDown;
        if (altDown)
        {
            _blockAnchorCellColumn = GetCellColumnFromPoint(e.GetPosition(this));
            _blockCurrentCellColumn = _blockAnchorCellColumn;
        }

        _selectionAnchor = position;
        _selectionAnchorPoint = e.GetPosition(this);
        _selectionDragStarted = false;
        _selection = new TerminalTextRange(position, position);
        CaptureMouse();
        InvalidateVisual();
        e.Handled = true;
    }

    private void SelectWord(int lineIndex, int textIndex)
    {
        _keyboardAnchor = null;
        if (lineIndex < 0 || lineIndex >= _lines.Count)
        {
            return;
        }

        LineLayout line = _lines[lineIndex];
        string text = line.Text;
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        textIndex = Math.Clamp(textIndex, 0, text.Length - 1);

        static bool IsWordChar(char ch) => char.IsLetterOrDigit(ch) || ch == '_';

        if (!IsWordChar(text[textIndex]))
        {
            SelectRange(new TerminalTextRange(
                new TerminalTextPosition(lineIndex, textIndex),
                new TerminalTextPosition(lineIndex, textIndex + 1)));
            return;
        }

        int start = textIndex;
        while (start > 0 && IsWordChar(text[start - 1]))
        {
            start--;
        }

        int end = textIndex;
        while (end < text.Length && IsWordChar(text[end]))
        {
            end++;
        }

        SelectRange(new TerminalTextRange(
            new TerminalTextPosition(lineIndex, start),
            new TerminalTextPosition(lineIndex, end)));
    }

    private void SelectLine(int lineIndex)
    {
        _keyboardAnchor = null;
        if (lineIndex < 0 || lineIndex >= _lines.Count)
        {
            return;
        }

        LineLayout line = _lines[lineIndex];
        SelectRange(new TerminalTextRange(
            new TerminalTextPosition(lineIndex, 0),
            new TerminalTextPosition(lineIndex, line.Text.Length)));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            // Plain hover (no drag): underline + hand-cursor the link under the pointer.
            UpdateHoveredLink(e.GetPosition(this));
            return;
        }

        // A drag is in progress — selection, not a link hover.
        SetHoveredLink(null);

        if (!_selectionAnchor.HasValue)
        {
            return;
        }

        Point currentPoint = e.GetPosition(this);
        if (!_selectionDragStarted && _selectionAnchorPoint.HasValue)
        {
            Vector delta = currentPoint - _selectionAnchorPoint.Value;
            _selectionDragStarted = Math.Abs(delta.X) >= SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(delta.Y) >= SystemParameters.MinimumVerticalDragDistance;
        }

        if (!TryCreateTextPosition(currentPoint, out TerminalTextPosition currentPosition))
        {
            return;
        }

        if (_blockSelectionMode)
        {
            _blockCurrentCellColumn = GetCellColumnFromPoint(currentPoint);
        }

        _selection = new TerminalTextRange(_selectionAnchor.Value, currentPosition);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        ReleaseMouseCapture();
        if (!_selectionAnchor.HasValue)
        {
            return;
        }

        // Hyperlink activation is handled on Ctrl+mouse-down (OnMouseLeftButtonDown), not here:
        // TerminalTabView releases the mouse capture in its PreviewMouseUp before this bubbling
        // handler runs, which clears _selectionAnchor — so the link gesture can't reliably fire here.
        TerminalTextRange? normalized = NormalizeSelection(_selection);
        if (!normalized.HasValue || normalized.Value.IsEmpty)
        {
            ClearSelection();
        }

        _selectionAnchor = null;
        _selectionAnchorPoint = null;
        _selectionDragStarted = false;
        // _blockSelectionMode is intentionally retained so drawing and copy continue to use block mode
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _selectionAnchor = null;
        _selectionAnchorPoint = null;
        _selectionDragStarted = false;
        _blockSelectionMode = false;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        SetHoveredLink(null);
    }

    /// <summary>Underlines + hand-cursors the link under <paramref name="point"/>, or clears the hover.</summary>
    private void UpdateHoveredLink(Point point)
    {
        if (TryCreateTextPosition(point, out TerminalTextPosition position) &&
            TryGetHyperlinkRegion(position.LineIndex, position.TextIndex, out _, out int startColumn, out int endColumn))
        {
            SetHoveredLink((position.LineIndex, startColumn, endColumn));
            Cursor = Cursors.Hand;
        }
        else
        {
            SetHoveredLink(null);
            ClearValue(CursorProperty);
        }
    }

    private void SetHoveredLink((int Line, int StartColumn, int EndColumn)? value)
    {
        if (_hoveredLink == value)
        {
            return;
        }

        _hoveredLink = value;
        InvalidateVisual();
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (e.Property == FontFamilyProperty ||
            e.Property == FontSizeProperty ||
            e.Property == FontStretchProperty ||
            e.Property == FontStyleProperty ||
            e.Property == FontWeightProperty)
        {
            _metricsDirty = true;
            UpdateScrollMetrics();
            InvalidateVisual();
            return;
        }

        if (e.Property == BackgroundProperty ||
            e.Property == ForegroundProperty)
        {
            InvalidateVisual();
        }
    }

    private void DrawLineBackgrounds(DrawingContext drawingContext, LineLayout line, double top, double contentLeft)
    {
        foreach (SegmentLayout segment in line.Segments)
        {
            Rect rect = new(
                contentLeft + (segment.StartCell * _cellSize.Width),
                top,
                Math.Max(0, segment.Snapshot.CellLength * _cellSize.Width),
                _cellSize.Height);
            drawingContext.DrawRectangle(GetBrush(segment.Snapshot.Background), null, rect);
        }
    }

    private void DrawSelection(
        DrawingContext drawingContext,
        TerminalTextRange? selection,
        int lineIndex,
        LineLayout line,
        double top,
        double contentLeft)
    {
        if (!selection.HasValue)
        {
            return;
        }

        TerminalTextRange range = selection.Value;
        if (lineIndex < range.Start.LineIndex || lineIndex > range.End.LineIndex)
        {
            return;
        }

        int startColumn = lineIndex == range.Start.LineIndex
            ? line.TextCellMap.GetCellColumn(range.Start.TextIndex, preferTrailingEdge: false)
            : 0;
        int endColumn = lineIndex == range.End.LineIndex
            ? line.TextCellMap.GetCellColumn(range.End.TextIndex, preferTrailingEdge: true)
            : line.CellLength;
        if (endColumn <= startColumn)
        {
            return;
        }

        Rect rect = new(
            contentLeft + (startColumn * _cellSize.Width),
            top,
            (endColumn - startColumn) * _cellSize.Width,
            _cellSize.Height);
        drawingContext.DrawRectangle(SelectionBackground ?? DefaultSelectionBrush, null, rect);
    }

    private void DrawBlockSelection(
        DrawingContext drawingContext,
        TerminalTextRange selection,
        int lineIndex,
        double top,
        double contentLeft)
    {
        if (lineIndex < selection.Start.LineIndex || lineIndex > selection.End.LineIndex)
        {
            return;
        }

        var (leftColumn, rightColumn) = GetBlockColumnRange();
        Rect rect = new(
            contentLeft + (leftColumn * _cellSize.Width),
            top,
            (rightColumn - leftColumn) * _cellSize.Width,
            _cellSize.Height);
        drawingContext.DrawRectangle(SelectionBackground ?? DefaultSelectionBrush, null, rect);
    }

    // Builds the cached, ready-to-paint commands for one line. Positions are relative to the line's
    // top-left (cell-based X, Y = 0); OnRender translates them by the current scroll/padding offsets.
    // The commands are shaped once and reused across repaints until the line content or font metrics
    // change, so OnRender no longer re-shapes every visible line on every frame.
    private LineDrawable BuildLineDrawable(LineLayout line)
    {
        var commands = new List<IDrawCommand>();
        foreach (SegmentLayout segment in line.Segments)
        {
            if (string.IsNullOrEmpty(segment.Snapshot.Text))
            {
                continue;
            }

            string segText = segment.Snapshot.Text;
            Typeface primaryTypeface = segment.Snapshot.Italic ? _italicTypeface! : _typeface!;
            FontWeight fontWeight = segment.Snapshot.Bold ? FontWeights.SemiBold : FontWeights.Regular;
            Brush foreground = GetBrush(segment.Snapshot.Foreground);
            TextDecorationCollection? decorations = BuildDecorations(segment.Snapshot);
            FontFallbackResolver? fallback = _fontFallback;
            bool ambiguousAsWide = _ambiguousAsWide;
            bool italic = segment.Snapshot.Italic;
            bool blink = segment.Snapshot.Blink;

            if (fallback is null)
            {
                commands.Add(CreateRunCommand(segText, primaryTypeface, isPrimary: true, fontWeight, foreground,
                    decorations, segment.StartCell * _cellSize.Width, blink));
                continue;
            }

            int[] starts = StringInfo.ParseCombiningCharacters(segText);
            double cellX = segment.StartCell * _cellSize.Width;
            int runStart = 0;
            double runCellX = cellX;
            GlyphTypeface? runGlyph = null;

            for (int i = 0; i < starts.Length; i++)
            {
                int elemStart = starts[i];
                int elemEnd = i + 1 < starts.Length ? starts[i + 1] : segText.Length;
                int codepoint = char.ConvertToUtf32(segText, elemStart);
                GlyphTypeface? resolved = fallback.Resolve(codepoint);

                if (i == 0)
                {
                    runGlyph = resolved;
                }
                else if (!ReferenceEquals(resolved, runGlyph))
                {
                    string runText = segText[runStart..elemStart];
                    Typeface tf = ResolveTypeface(runGlyph, primaryTypeface, italic);
                    commands.Add(CreateRunCommand(runText, tf, IsPrimaryGlyph(runGlyph), fontWeight, foreground,
                        decorations, runCellX, blink));
                    runStart = elemStart;
                    runCellX = cellX;
                    runGlyph = resolved;
                }

                string elem = segText[elemStart..elemEnd];
                cellX += EstimateTextElementCellWidth(elem, ambiguousAsWide) * _cellSize.Width;
            }

            if (runStart < segText.Length)
            {
                string runText = segText[runStart..];
                Typeface tf = ResolveTypeface(runGlyph, primaryTypeface, italic);
                commands.Add(CreateRunCommand(runText, tf, IsPrimaryGlyph(runGlyph), fontWeight, foreground,
                    decorations, runCellX, blink));
            }
        }

        return new LineDrawable(commands.ToArray());
    }

    private bool IsPrimaryGlyph(GlyphTypeface? glyphTypeface)
        => glyphTypeface is null || ReferenceEquals(glyphTypeface, _primaryGlyphTypeface);

    // Builds a position-relative paint command for one same-font run. With ligatures enabled,
    // primary-font runs are shaped through TextFormatter (honoring OpenType liga/calt); wide and
    // fallback runs always use FormattedText so they keep exact cell-by-cell placement.
    private IDrawCommand CreateRunCommand(
        string text,
        Typeface typeface,
        bool isPrimary,
        FontWeight fontWeight,
        Brush foreground,
        TextDecorationCollection? decorations,
        double relativeX,
        bool blink)
    {
        if (_fontLigaturesEnabled && isPrimary)
        {
            TextLine line = FormatLigatureRun(text, typeface, fontWeight, foreground, decorations);
            return new TextLineCommand(line, relativeX, blink);
        }

        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            FontSize,
            foreground,
            _pixelsPerDip);
        formatted.SetFontWeight(fontWeight);
        if (decorations is not null)
        {
            formatted.SetTextDecorations(decorations);
        }

        return new FormattedTextCommand(formatted, relativeX, blink);
    }

    private TextLine FormatLigatureRun(
        string text,
        Typeface typeface,
        FontWeight fontWeight,
        Brush foreground,
        TextDecorationCollection? decorations)
    {
        Typeface weighted = typeface.Weight == fontWeight
            ? typeface
            : new Typeface(typeface.FontFamily, typeface.Style, fontWeight, typeface.Stretch);
        var runProperties = new LigatureRunProperties(weighted, FontSize, _pixelsPerDip, foreground, decorations);
        var paragraphProperties = new LineParagraphProperties(runProperties);
        var source = new SingleRunTextSource(text, runProperties);
        _textFormatter ??= TextFormatter.Create(TextFormattingMode.Display);
        return _textFormatter.FormatLine(source, 0, LigatureParagraphWidth, paragraphProperties, null);
    }

    private const double LigatureParagraphWidth = 1_000_000.0;

    private static Typeface ResolveTypeface(GlyphTypeface? glyphTypeface, Typeface primaryTypeface, bool italic)
    {
        if (glyphTypeface is null)
        {
            return primaryTypeface;
        }

        primaryTypeface.TryGetGlyphTypeface(out GlyphTypeface? primaryGtf);
        if (ReferenceEquals(glyphTypeface, primaryGtf))
        {
            return primaryTypeface;
        }

        string familyName = glyphTypeface.FamilyNames.Values.FirstOrDefault()
            ?? glyphTypeface.Win32FamilyNames.Values.FirstOrDefault()
            ?? string.Empty;
        return new Typeface(
            new FontFamily(familyName),
            italic ? FontStyles.Italic : FontStyles.Normal,
            FontWeights.Regular,
            FontStretches.Normal);
    }

    private static TextDecorationCollection? BuildDecorations(AnsiTerminalBuffer.TerminalRenderSegmentSnapshot snapshot)
    {
        if (snapshot.UnderlineStyle == UnderlineStyle.None && !snapshot.Strikethrough && !snapshot.Overline)
        {
            return null;
        }

        var decorations = new TextDecorationCollection();
        if (snapshot.UnderlineStyle != UnderlineStyle.None)
        {
            AnsiTerminalBuffer.AddUnderlineDecorations(decorations, snapshot.UnderlineStyle, snapshot.UnderlineColor, snapshot.Foreground);
        }

        if (snapshot.Strikethrough) foreach (TextDecoration d in TextDecorations.Strikethrough) decorations.Add(d);
        if (snapshot.Overline) foreach (TextDecoration d in TextDecorations.OverLine) decorations.Add(d);
        return decorations;
    }

    private void EnsureMetrics()
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        double pixelsPerDip = dpi.PixelsPerDip;
        if (!_metricsDirty &&
            DoubleUtil.AreClose(_pixelsPerDip, pixelsPerDip) &&
            _typeface is not null)
        {
            return;
        }

        _pixelsPerDip = pixelsPerDip;
        _typeface = new Typeface(FontFamily, FontStyle, FontWeight, FontStretch);
        _italicTypeface = new Typeface(FontFamily, FontStyles.Italic, FontWeight, FontStretch);
        _typeface.TryGetGlyphTypeface(out _primaryGlyphTypeface);
        _fontFallback = new FontFallbackResolver(_typeface);
        // Font metrics changed, so any glyphs shaped against the old typeface/size are stale.
        _lines.InvalidateDrawables();
        var text = new FormattedText(
            "W",
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            _typeface,
            FontSize,
            Foreground ?? DefaultForegroundBrush,
            _pixelsPerDip);
        double measuredHeight = Math.Max(1.0, text.Height);
        _cellSize = new Size(
            Math.Max(1.0, text.WidthIncludingTrailingWhitespace),
            SnapToDevicePixelsUp(measuredHeight, dpi.DpiScaleY));
        _metricsDirty = false;
    }

    private static double SnapToDevicePixelsUp(double value, double dpiScale)
    {
        if (!double.IsFinite(value) || value <= 0 || !double.IsFinite(dpiScale) || dpiScale <= 0)
        {
            return Math.Max(1.0, value);
        }

        return Math.Ceiling(value * dpiScale) / dpiScale;
    }

    private void UpdateViewport(Size size)
    {
        double nextWidth = double.IsInfinity(size.Width) ? 0 : Math.Max(0, size.Width);
        double nextHeight = double.IsInfinity(size.Height) ? 0 : Math.Max(0, size.Height);
        if (DoubleUtil.AreClose(_viewportWidth, nextWidth) &&
            DoubleUtil.AreClose(_viewportHeight, nextHeight))
        {
            return;
        }

        _viewportWidth = nextWidth;
        _viewportHeight = nextHeight;
        // The extent height folds in the viewport's sub-row remainder, so recompute it whenever
        // the viewport size changes rather than only on snapshot/floor updates.
        UpdateScrollMetrics();
        ScrollOwner?.InvalidateScrollInfo();
    }

    private void UpdateScrollMetrics()
    {
        EnsureMetrics();
        Thickness padding = Padding;
        _extentWidth = Math.Max(
            _viewportFloorWidth,
            padding.Left + padding.Right + (_maxCellLength * _cellSize.Width));
        // The visible row count is a floor division of the viewport height by the cell height,
        // so the viewport is usually a few pixels taller than a whole number of rows. Park that
        // sub-row remainder below the last line by padding the extent with it: scrolling to the
        // bottom then aligns rows to the viewport's top edge (e.g. after Ctrl+L) instead of
        // revealing a stale sliver of the line above the active screen.
        _extentHeight = Math.Max(
            _viewportFloorHeight,
            padding.Top + padding.Bottom + (_lines.Count * _cellSize.Height) + ComputeSubCellViewportRemainder(padding));
        CoerceOffsets();
        if (!DoubleUtil.AreClose(_extentWidth, _lastReportedExtentWidth) ||
            !DoubleUtil.AreClose(_extentHeight, _lastReportedExtentHeight))
        {
            if (ScrollOwner is not null)
            {
                _lastReportedExtentWidth = _extentWidth;
                _lastReportedExtentHeight = _extentHeight;
                ScrollOwner.InvalidateScrollInfo();
            }
        }
    }

    private double ComputeSubCellViewportRemainder(Thickness padding)
    {
        double cellHeight = _cellSize.Height;
        if (cellHeight <= 0)
        {
            return 0;
        }

        double contentViewportHeight = _viewportHeight - padding.Top - padding.Bottom;
        if (contentViewportHeight <= 0)
        {
            return 0;
        }

        double rows = contentViewportHeight / cellHeight;
        double fraction = rows - Math.Floor(rows);
        // Treat a viewport that is (within rounding) an exact multiple of the cell height as
        // having no remainder so floating-point error never inflates the extent by a full row.
        if (fraction < 1e-6 || fraction > 1 - 1e-6)
        {
            return 0;
        }

        return fraction * cellHeight;
    }

    private Size ResolveViewportSize(Size constraint)
    {
        return new Size(
            double.IsInfinity(constraint.Width) ? _viewportFloorWidth : Math.Max(constraint.Width, _viewportFloorWidth),
            double.IsInfinity(constraint.Height) ? _viewportFloorHeight : Math.Max(constraint.Height, _viewportFloorHeight));
    }

    private static double NormalizeViewportFloorExtent(double value)
    {
        return double.IsFinite(value) && value > 0 ? value : 0;
    }

    private void CoerceOffsets()
    {
        _horizontalOffset = Math.Clamp(_horizontalOffset, 0, Math.Max(0, _extentWidth - _viewportWidth));
        _verticalOffset = Math.Clamp(_verticalOffset, 0, Math.Max(0, _extentHeight - _viewportHeight));
    }

    private void CoerceSelection()
    {
        if (!_selection.HasValue)
        {
            return;
        }

        TerminalTextRange range = _selection.Value;
        if (_lines.Count == 0)
        {
            _selection = null;
            return;
        }

        _selection = new TerminalTextRange(
            CoerceTextPosition(range.Start),
            CoerceTextPosition(range.End));
    }

    private void CoerceKeyboardCursor()
    {
        _keyboardCursor = _lines.Count == 0
            ? new TerminalTextPosition(0, 0)
            : CoerceTextPosition(_keyboardCursor);
        if (_keyboardAnchor.HasValue)
        {
            _keyboardAnchor = _lines.Count == 0
                ? new TerminalTextPosition(0, 0)
                : CoerceTextPosition(_keyboardAnchor.Value);
        }
    }

    private TerminalTextPosition CoerceTextPosition(TerminalTextPosition position)
    {
        if (_lines.Count == 0)
        {
            return new TerminalTextPosition(0, 0);
        }

        int lineIndex = Math.Clamp(position.LineIndex, 0, _lines.Count - 1);
        int textIndex = Math.Clamp(position.TextIndex, 0, _lines[lineIndex].Text.Length);
        return new TerminalTextPosition(lineIndex, textIndex);
    }

    private void SelectRange(TerminalTextRange range)
    {
        _blockSelectionMode = false;
        _selection = NormalizeSelection(range);
        BringSelectionIntoView();
        InvalidateVisual();
    }

    private void BringSelectionIntoView()
    {
        TerminalTextRange? selection = NormalizeSelection(_selection);
        if (!selection.HasValue)
        {
            return;
        }

        TerminalTextRange range = selection.Value;
        if (range.Start.LineIndex >= _lines.Count)
        {
            return;
        }

        LineLayout line = _lines[range.Start.LineIndex];
        int startColumn = line.TextCellMap.GetCellColumn(range.Start.TextIndex, preferTrailingEdge: false);
        int endColumn = range.End.LineIndex == range.Start.LineIndex
            ? line.TextCellMap.GetCellColumn(range.End.TextIndex, preferTrailingEdge: true)
            : startColumn + 1;
        Rect startRect = GetCellRect(range.Start.LineIndex, startColumn);
        Rect endRect = GetCellRect(range.Start.LineIndex, Math.Max(startColumn + 1, endColumn));
        MakeVisible(this, new Rect(startRect.TopLeft, endRect.BottomRight));
    }

    private bool TryCreateTextPosition(Point point, out TerminalTextPosition position)
    {
        position = default;
        if (!TryGetTextPositionFromPoint(point, out int lineIndex, out int textIndex))
        {
            return false;
        }

        position = new TerminalTextPosition(lineIndex, textIndex);
        return true;
    }

    private bool TryGetHyperlink(int lineIndex, int textIndex, out string? target)
        => TryGetHyperlinkRegion(lineIndex, textIndex, out target, out _, out _);

    /// <summary>
    /// Resolves the clickable target at a text position and the cell-column span it occupies on the
    /// line (used to draw the hover underline). Matches an OSC 8 hyperlink segment first, then a
    /// bare URL/file path detected in the plain text.
    /// </summary>
    private bool TryGetHyperlinkRegion(int lineIndex, int textIndex, out string? target, out int startColumn, out int endColumn)
    {
        target = null;
        startColumn = 0;
        endColumn = 0;
        if (lineIndex < 0 || lineIndex >= _lines.Count)
        {
            return false;
        }

        LineLayout line = _lines[lineIndex];
        int column = line.TextCellMap.GetCellColumn(textIndex, preferTrailingEdge: false);
        foreach (SegmentLayout segment in line.Segments)
        {
            if (segment.Snapshot.Hyperlink is null)
            {
                continue;
            }

            int start = segment.StartCell;
            int end = start + segment.Snapshot.CellLength;
            if (column < start || column >= end)
            {
                continue;
            }

            // Explicit OSC 8 hyperlink — its target is the embedded URI.
            target = segment.Snapshot.Hyperlink;
            startColumn = start;
            endColumn = end;
            return true;
        }

        // No explicit OSC 8 hyperlink at this position — fall back to detecting a bare URL
        // (e.g. "https://example.com") or a file path printed as plain text on the line.
        if (!TryDetectTargetAt(line.Text, textIndex, out target, out int textStart, out int textLength))
        {
            return false;
        }

        startColumn = line.TextCellMap.GetCellColumn(textStart, preferTrailingEdge: false);
        endColumn = line.TextCellMap.GetCellColumn(textStart + textLength, preferTrailingEdge: false);
        return true;
    }

    private static readonly Regex UrlPattern = new(
        @"(?:https?|ftp|file)://[^\s<>""'` ]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A Windows/UNC/Unix file path, optionally followed by :line or :line:col. The first
    // alternative requires a recognizable root (drive, UNC, ./ ../ or a leading slash); the second
    // accepts a relative path that has a separator and a file extension, so ordinary words aren't
    // mistaken for paths (e.g. "src/Foo.cs:12").
    private static readonly Regex FilePathPattern = new(
        @"(?:[A-Za-z]:[\\/]|\\\\[^\s\\/]+[\\/]|\.{0,2}[\\/])[^\s:*?""<>|]+(?:[\\/][^\s:*?""<>|]+)*(?::\d+(?::\d+)?)?" +
        @"|[\w.\-]+(?:[\\/][\w.\-]+)+\.\w+(?::\d+(?::\d+)?)?",
        RegexOptions.Compiled);

    /// <summary>
    /// Scans <paramref name="text"/> for a clickable target (URL or file path) whose span contains
    /// <paramref name="textIndex"/>. URLs take precedence over file paths. Trailing sentence
    /// punctuation is trimmed. The raw matched text is returned so the host can route it.
    /// </summary>
    private static bool TryDetectTargetAt(string text, int textIndex, out string? target, out int matchStart, out int matchLength)
    {
        target = null;
        matchStart = 0;
        matchLength = 0;
        if (string.IsNullOrEmpty(text) || textIndex < 0 || textIndex >= text.Length)
        {
            return false;
        }

        return TryMatchAt(UrlPattern, text, textIndex, out target, out matchStart, out matchLength)
            || TryMatchAt(FilePathPattern, text, textIndex, out target, out matchStart, out matchLength);
    }

    private static bool TryMatchAt(Regex pattern, string text, int textIndex, out string? match, out int matchStart, out int matchLength)
    {
        match = null;
        matchStart = 0;
        matchLength = 0;
        foreach (Match m in pattern.Matches(text))
        {
            int start = m.Index;
            int length = TrimTrailingPunctuation(m.Value);
            if (length <= 0 || textIndex < start || textIndex >= start + length)
            {
                continue;
            }

            match = text.Substring(start, length);
            matchStart = start;
            matchLength = length;
            return true;
        }

        return false;
    }

    private static int TrimTrailingPunctuation(string url)
    {
        int length = url.Length;
        while (length > 0)
        {
            char c = url[length - 1];
            if (c is '.' or ',' or ';' or ':' or '!' or '?' or ')' or ']' or '}' or '>' or '"' or '\'')
            {
                length--;
            }
            else
            {
                break;
            }
        }

        return length;
    }

    private bool TryFindForward(TerminalTextPosition start, string query, StringComparison comparison, out TerminalTextRange range)
    {
        start = CoerceTextPosition(start);
        for (int lineIndex = start.LineIndex; lineIndex < _lines.Count; lineIndex++)
        {
            LineLayout line = _lines[lineIndex];
            int searchStart = lineIndex == start.LineIndex ? Math.Clamp(start.TextIndex, 0, line.Text.Length) : 0;
            int found = line.Text.IndexOf(query, searchStart, comparison);
            if (found < 0)
            {
                continue;
            }

            range = new TerminalTextRange(
                new TerminalTextPosition(lineIndex, found),
                new TerminalTextPosition(lineIndex, found + query.Length));
            return true;
        }

        range = default;
        return false;
    }

    private bool TryFindBackward(TerminalTextPosition start, string query, StringComparison comparison, out TerminalTextRange range)
    {
        start = CoerceTextPosition(start);
        for (int lineIndex = start.LineIndex; lineIndex >= 0; lineIndex--)
        {
            LineLayout line = _lines[lineIndex];
            int searchLimit = lineIndex == start.LineIndex
                ? Math.Clamp(start.TextIndex, 0, line.Text.Length)
                : line.Text.Length;
            if (searchLimit == 0)
            {
                continue;
            }

            int found = FindLastIndex(line.Text, query, searchLimit, comparison);
            if (found < 0)
            {
                continue;
            }

            range = new TerminalTextRange(
                new TerminalTextPosition(lineIndex, found),
                new TerminalTextPosition(lineIndex, found + query.Length));
            return true;
        }

        range = default;
        return false;
    }

    private static int FindLastIndex(string text, string query, int searchLimit, StringComparison comparison)
    {
        int maxStart = searchLimit - query.Length;
        for (int index = maxStart; index >= 0; index--)
        {
            if (string.Compare(text, index, query, 0, query.Length, comparison) == 0)
            {
                return index;
            }
        }

        return -1;
    }

    private static TerminalTextRange? NormalizeSelection(TerminalTextRange? selection)
    {
        if (!selection.HasValue)
        {
            return null;
        }

        TerminalTextRange range = selection.Value;
        if (range.Start.CompareTo(range.End) <= 0)
        {
            return range;
        }

        return new TerminalTextRange(range.End, range.Start);
    }

    private static LineLayout CreateLineLayout(AnsiTerminalBuffer.TerminalRenderLineSnapshot line, bool ambiguousAsWide)
    {
        string text = string.Concat(line.Segments.Select(static segment => segment.Text));
        var segments = new SegmentLayout[line.Segments.Length];
        int cellOffset = 0;

        for (int index = 0; index < line.Segments.Length; index++)
        {
            AnsiTerminalBuffer.TerminalRenderSegmentSnapshot seg = line.Segments[index];
            segments[index] = new SegmentLayout(cellOffset, seg);

            cellOffset += seg.CellLength;
        }
        return new LineLayout(
            text,
            line.CellLength,
            segments,
            TerminalTextCellMap.Create(
                line.Segments.Select(static segment => (segment.Text, segment.CellLength)),
                line.CellLength,
                ambiguousAsWide));
    }

    private Brush GetBrush(Color color)
    {
        if (_brushCache.TryGetValue(color, out SolidColorBrush? brush))
        {
            return brush;
        }

        brush = CreateFrozenBrush(color);
        _brushCache[color] = brush;
        return brush;
    }

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static int GetDisplayWidth(Rune rune, bool ambiguousAsWide) =>
        UnicodeWidth.GetWidth(rune, ambiguousAsWide);

    private static int EstimateTextElementCellWidth(string element, bool ambiguousAsWide)
    {
        bool hasVisibleRune = false;
        int maxWidth = 1;
        foreach (Rune rune in element.EnumerateRunes())
        {
            int width = GetDisplayWidth(rune, ambiguousAsWide);
            if (width <= 0)
            {
                continue;
            }

            hasVisibleRune = true;
            maxWidth = Math.Max(maxWidth, width);
        }

        return hasVisibleRune ? maxWidth : 1;
    }

    // Lazily builds and caches LineLayout objects (and, alongside each, the shaped paint commands)
    // for the current render snapshot. Layouts for unchanged lines are reused across snapshots;
    // entries outside the viewport window are evicted, so memory and per-update CPU scale with the
    // visible region rather than the whole scrollback. The shaped drawable rides in the same entry,
    // so it is reused across repaints and discarded together with its layout on eviction.
    private sealed class VirtualLineLayouts
    {
        private sealed class Entry(LineLayout layout)
        {
            public LineLayout Layout { get; } = layout;
            public LineDrawable? Drawable { get; set; }
        }

        private readonly Dictionary<int, Entry> _cache = [];
        private readonly List<int> _evictionScratch = [];
        private AnsiTerminalBuffer.TerminalRenderLineSnapshot[] _snapshot = [];
        private bool _ambiguousAsWide;

        public int Count => _snapshot.Length;

        public int CachedCount => _cache.Count;

        public int CachedDrawableCount
        {
            get
            {
                int count = 0;
                foreach (Entry entry in _cache.Values)
                {
                    if (entry.Drawable is not null)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        public LineLayout this[int index] => GetEntry(index).Layout;

        private Entry GetEntry(int index)
        {
            if (!_cache.TryGetValue(index, out Entry? entry))
            {
                entry = new Entry(CreateLineLayout(_snapshot[index], _ambiguousAsWide));
                _cache[index] = entry;
            }

            return entry;
        }

        // Returns the shaped paint commands for a line, building them once via <paramref name="builder"/>
        // and reusing them on subsequent repaints until the entry is evicted or invalidated.
        public LineDrawable GetDrawable(int index, Func<LineLayout, LineDrawable> builder)
        {
            Entry entry = GetEntry(index);
            return entry.Drawable ??= builder(entry.Layout);
        }

        // Drops every cached drawable (keeping the layouts) so the next repaint re-shapes against the
        // current font metrics or ligature setting.
        public void InvalidateDrawables()
        {
            foreach (Entry entry in _cache.Values)
            {
                entry.Drawable?.Dispose();
                entry.Drawable = null;
            }
        }

        // Adopts a new snapshot and returns the maximum cell length across all lines (for the
        // horizontal scroll extent). Cached entries are kept only where the line content is
        // unchanged, so new output at the bottom reuses the scrollback layouts above it.
        public int SetSnapshot(AnsiTerminalBuffer.TerminalRenderLineSnapshot[] lines, bool ambiguousAsWide)
        {
            AnsiTerminalBuffer.TerminalRenderLineSnapshot[] previous = _snapshot;
            bool reuse = ambiguousAsWide == _ambiguousAsWide && _cache.Count > 0;

            int maxCellLength = 0;
            for (int index = 0; index < lines.Length; index++)
            {
                if (lines[index].CellLength > maxCellLength)
                {
                    maxCellLength = lines[index].CellLength;
                }
            }

            _snapshot = lines;
            _ambiguousAsWide = ambiguousAsWide;

            if (!reuse)
            {
                DisposeAndClear();
                return maxCellLength;
            }

            _evictionScratch.Clear();
            foreach (int index in _cache.Keys)
            {
                if (index >= lines.Length || index >= previous.Length || !lines[index].ContentEquals(previous[index]))
                {
                    _evictionScratch.Add(index);
                }
            }

            Evict();
            return maxCellLength;
        }

        // Evicts cached entries whose line index falls outside [startInclusive, endInclusive].
        public void TrimOutsideWindow(int startInclusive, int endInclusive)
        {
            if (_cache.Count == 0)
            {
                return;
            }

            _evictionScratch.Clear();
            foreach (int index in _cache.Keys)
            {
                if (index < startInclusive || index > endInclusive)
                {
                    _evictionScratch.Add(index);
                }
            }

            Evict();
        }

        private void Evict()
        {
            foreach (int index in _evictionScratch)
            {
                if (_cache.Remove(index, out Entry? entry))
                {
                    entry.Drawable?.Dispose();
                }
            }
        }

        private void DisposeAndClear()
        {
            foreach (Entry entry in _cache.Values)
            {
                entry.Drawable?.Dispose();
            }

            _cache.Clear();
        }
    }

    // A line's shaped, position-relative paint commands, reused across repaints. Disposing it
    // releases any unmanaged text resources held by its commands (e.g. TextLine).
    private sealed class LineDrawable(IDrawCommand[] commands) : IDisposable
    {
        public IDrawCommand[] Commands { get; } = commands;

        public void Dispose()
        {
            foreach (IDrawCommand command in Commands)
            {
                if (command is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
        }
    }

    // A single run's paint operation, positioned relative to its line's top-left. OnRender supplies
    // the current (offsetX, offsetY) to translate it into device coordinates.
    private interface IDrawCommand
    {
        // True when the run carries the SGR 5/6 blink attribute; OnRender skips it on the blink-off
        // phase so the text disappears and reappears on the shared cadence.
        bool Blink { get; }

        void Render(DrawingContext drawingContext, double offsetX, double offsetY);
    }

    private sealed class FormattedTextCommand(FormattedText text, double relativeX, bool blink) : IDrawCommand
    {
        public bool Blink => blink;

        public void Render(DrawingContext drawingContext, double offsetX, double offsetY)
            => drawingContext.DrawText(text, new Point(relativeX + offsetX, offsetY));
    }

    private sealed class TextLineCommand(TextLine line, double relativeX, bool blink) : IDrawCommand, IDisposable
    {
        public bool Blink => blink;

        public void Render(DrawingContext drawingContext, double offsetX, double offsetY)
            => line.Draw(drawingContext, new Point(relativeX + offsetX, offsetY), InvertAxes.None);

        public void Dispose() => line.Dispose();
    }

    // Feeds a single styled run to TextFormatter.FormatLine, followed by an end-of-paragraph marker.
    private sealed class SingleRunTextSource(string text, TextRunProperties properties) : TextSource
    {
        public override TextRun GetTextRun(int textSourceCharacterIndex)
        {
            if (textSourceCharacterIndex >= text.Length)
            {
                return new TextEndOfParagraph(1);
            }

            return new TextCharacters(text, textSourceCharacterIndex, text.Length - textSourceCharacterIndex, properties);
        }

        public override TextSpan<CultureSpecificCharacterBufferRange> GetPrecedingText(int textSourceCharacterIndexLimit)
            => new(0, new CultureSpecificCharacterBufferRange(
                properties.CultureInfo,
                new CharacterBufferRange(string.Empty, 0, 0)));

        public override int GetTextEffectCharacterIndexFromTextSourceCharacterIndex(int textSourceCharacterIndex)
            => textSourceCharacterIndex;
    }

    private sealed class LineParagraphProperties(TextRunProperties defaultProperties) : TextParagraphProperties
    {
        public override FlowDirection FlowDirection => FlowDirection.LeftToRight;
        public override TextAlignment TextAlignment => TextAlignment.Left;
        public override double LineHeight => 0;
        public override bool FirstLineInParagraph => false;
        public override TextRunProperties DefaultTextRunProperties => defaultProperties;
        public override TextWrapping TextWrapping => TextWrapping.NoWrap;
        public override TextMarkerProperties? TextMarkerProperties => null;
        public override double Indent => 0;
    }

    private sealed class LigatureRunProperties : TextRunProperties
    {
        private static readonly TextRunTypographyProperties Typography = new LigatureTypographyProperties();

        private readonly Typeface _typeface;
        private readonly double _emSize;
        private readonly Brush _foreground;
        private readonly TextDecorationCollection? _decorations;

        public LigatureRunProperties(
            Typeface typeface,
            double emSize,
            double pixelsPerDip,
            Brush foreground,
            TextDecorationCollection? decorations)
        {
            _typeface = typeface;
            _emSize = emSize;
            _foreground = foreground;
            _decorations = decorations;
            PixelsPerDip = pixelsPerDip;
        }

        public override Typeface Typeface => _typeface;
        public override double FontRenderingEmSize => _emSize;
        public override double FontHintingEmSize => _emSize;
        public override TextDecorationCollection? TextDecorations => _decorations;
        public override Brush ForegroundBrush => _foreground;
        public override Brush? BackgroundBrush => null;
        public override CultureInfo CultureInfo => CultureInfo.CurrentCulture;
        public override TextEffectCollection? TextEffects => null;
        public override TextRunTypographyProperties TypographyProperties => Typography;
    }

    // OpenType feature set enabling programming-font ligatures: standard/contextual ligatures plus
    // contextual alternates (which fonts like FiraCode use to assemble multi-character glyphs). All
    // other features stay at their neutral defaults so glyph advances are otherwise unchanged.
    private sealed class LigatureTypographyProperties : TextRunTypographyProperties
    {
        public override bool StandardLigatures => true;
        public override bool ContextualLigatures => true;
        public override bool ContextualAlternates => true;
        public override bool DiscretionaryLigatures => false;
        public override bool HistoricalLigatures => false;
        public override bool HistoricalForms => false;
        public override bool Kerning => false;
        public override bool CapitalSpacing => false;
        public override bool CaseSensitiveForms => false;
        public override bool SlashedZero => false;
        public override bool MathematicalGreek => false;
        public override bool EastAsianExpertForms => false;
        public override int AnnotationAlternates => 0;
        public override int StandardSwashes => 0;
        public override int ContextualSwashes => 0;
        public override int StylisticAlternates => 0;
        public override FontFraction Fraction => FontFraction.Normal;
        public override FontVariants Variants => FontVariants.Normal;
        public override FontCapitals Capitals => FontCapitals.Normal;
        public override FontNumeralStyle NumeralStyle => FontNumeralStyle.Normal;
        public override FontNumeralAlignment NumeralAlignment => FontNumeralAlignment.Normal;
        public override FontEastAsianWidths EastAsianWidths => FontEastAsianWidths.Normal;
        public override FontEastAsianLanguage EastAsianLanguage => FontEastAsianLanguage.Normal;
        public override bool StylisticSet1 => false;
        public override bool StylisticSet2 => false;
        public override bool StylisticSet3 => false;
        public override bool StylisticSet4 => false;
        public override bool StylisticSet5 => false;
        public override bool StylisticSet6 => false;
        public override bool StylisticSet7 => false;
        public override bool StylisticSet8 => false;
        public override bool StylisticSet9 => false;
        public override bool StylisticSet10 => false;
        public override bool StylisticSet11 => false;
        public override bool StylisticSet12 => false;
        public override bool StylisticSet13 => false;
        public override bool StylisticSet14 => false;
        public override bool StylisticSet15 => false;
        public override bool StylisticSet16 => false;
        public override bool StylisticSet17 => false;
        public override bool StylisticSet18 => false;
        public override bool StylisticSet19 => false;
        public override bool StylisticSet20 => false;
    }

    private readonly record struct LineLayout(
        string Text,
        int CellLength,
        SegmentLayout[] Segments,
        TerminalTextCellMap TextCellMap);

    private readonly record struct SegmentLayout(
        int StartCell,
        AnsiTerminalBuffer.TerminalRenderSegmentSnapshot Snapshot);

    private readonly record struct TerminalTextPosition(int LineIndex, int TextIndex) : IComparable<TerminalTextPosition>
    {
        public int CompareTo(TerminalTextPosition other)
        {
            int lineCompare = LineIndex.CompareTo(other.LineIndex);
            return lineCompare != 0 ? lineCompare : TextIndex.CompareTo(other.TextIndex);
        }
    }

    private readonly record struct TerminalTextRange(TerminalTextPosition Start, TerminalTextPosition End)
    {
        public bool IsEmpty => Start.LineIndex == End.LineIndex && Start.TextIndex == End.TextIndex;
    }

    private static class DoubleUtil
    {
        public static bool AreClose(double left, double right)
        {
            return Math.Abs(left - right) < 0.01;
        }
    }
}

public sealed class TerminalHyperlinkActivatedEventArgs(string target) : EventArgs
{
    /// <summary>
    /// The raw clickable text under the cursor — an OSC 8 hyperlink URI, a detected URL, or a
    /// detected file path (which may carry a <c>:line</c> or <c>:line:col</c> suffix). A host can
    /// inspect this to route the activation (e.g. open a URL in an in-app browser and a file path
    /// in an editor); otherwise the default behavior opens it with the OS shell via <c>Process.Start</c>.
    /// </summary>
    public string Target { get; } = target;

    /// <summary>
    /// Set to <c>true</c> in a <c>TerminalTabView.HyperlinkActivated</c> handler to suppress the
    /// default behavior (opening <see cref="Target"/> with the OS shell via <c>Process.Start</c>).
    /// </summary>
    public bool Handled { get; set; }
}
