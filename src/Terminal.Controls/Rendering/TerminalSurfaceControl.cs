using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using System.Windows.Threading;
using System.Windows.Automation.Peers;

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
    private readonly TerminalLineRenderCache<LineDrawable> _lines = new();
    private IReadOnlyList<TerminalSelectionLine> SelectionLines => new SelectionLineList(_lines);
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
    private readonly TerminalSelectionSearchModel _selectionModel = new();
    private TerminalTextRange? _selection
    {
        get => _selectionModel.Selection;
        set => _selectionModel.Selection = value;
    }
    private TerminalTextPosition? _selectionAnchor;
    private Point? _selectionAnchorPoint;
    private bool _selectionDragStarted;
    private bool _blockSelectionMode
    {
        get => _selectionModel.IsBlockSelection;
        set => _selectionModel.IsBlockSelection = value;
    }
    // The link (URL/file path) currently under the mouse pointer, drawn with an underline so it
    // reads as clickable. Cell-column span on a single line; null when not hovering a link.
    private (int Line, int StartColumn, int EndColumn)? _hoveredLink;
    private double _blockAnchorCellColumn
    {
        get => _selectionModel.BlockAnchorColumn;
        set => _selectionModel.BlockAnchorColumn = value;
    }
    private double _blockCurrentCellColumn
    {
        get => _selectionModel.BlockCurrentColumn;
        set => _selectionModel.BlockCurrentColumn = value;
    }
    private TerminalTextPosition _keyboardCursor;
    private TerminalTextPosition? _keyboardAnchor;
    private readonly TerminalScrollState _scrollState = new();
    private double _lastReportedExtentWidth;
    private double _lastReportedExtentHeight;
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
        Unloaded += (_, _) =>
        {
            StopBlinkTimer();
            TextFormatter? formatter = _textFormatter;
            _textFormatter = null;
            var cleanup = new List<Action> { _lines.Clear };
            if (formatter is not null)
            {
                cleanup.Add(formatter.Dispose);
            }

            DisposableResourceOwner.ExecuteAllBestEffort(cleanup);
        };
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new TerminalSurfaceAutomationPeer(this);

    internal string GetAutomationText() => string.Join("\r\n", SelectionLines.Select(static line => line.Text));

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

    public double ExtentWidth => _scrollState.ExtentWidth;

    public double ExtentHeight => _scrollState.ExtentHeight;

    public double ViewportWidth => _scrollState.ViewportWidth;

    public double ViewportHeight => _scrollState.ViewportHeight;

    public double HorizontalOffset => _scrollState.HorizontalOffset;

    public double VerticalOffset => _scrollState.VerticalOffset;

    public ScrollViewer? ScrollOwner { get; set; }

    public bool HasSelection => _selectionModel.HasSelection;

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

    internal bool HasTextFormatter => _textFormatter is not null;

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
        if (!_scrollState.SetViewportFloor(size.Width, size.Height))
        {
            return;
        }

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

        _selectionModel.ClearSelection();
        _keyboardAnchor = null;
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
                TerminalLineLayout line = _lines[lineIndex];
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
        return TerminalSelectionSearchModel.ExtractText(
            SelectionLines,
            _selection,
            _blockSelectionMode,
            _blockAnchorCellColumn,
            _blockCurrentCellColumn);
    }

    private (int Left, int Right) GetBlockColumnRange() =>
        TerminalSelectionSearchModel.GetBlockColumns(_blockAnchorCellColumn, _blockCurrentCellColumn);

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
            TerminalLineLayout line = _lines[lineIndex];
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
    private static List<StyledRun> BuildStyledRuns(TerminalLineLayout line, int startTextIndex, int endTextIndex)
    {
        var runs = new List<StyledRun>();
        if (endTextIndex <= startTextIndex)
        {
            return runs;
        }

        int charOffset = 0;
        foreach (TerminalLineSegmentLayout segment in line.Segments)
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
        => TerminalSelectionSearchModel.CountMatches(SelectionLines, query, comparison);

    /// <summary>
    /// バッファ全体から <paramref name="query"/> の一致をすべて列挙する（行頭→行末、上から下へ）。
    /// 同一行に複数あれば重なりなく順に返す。選択状態やスクロール位置は変更しない。
    /// </summary>
    public IReadOnlyList<TerminalMatch> FindMatches(string query, StringComparison comparison)
        => TerminalSelectionSearchModel.FindMatches(SelectionLines, query, comparison);

    /// <summary>
    /// 指定位置の範囲を選択ハイライトし、その箇所までスクロールして可視化する
    /// （<see cref="FindMatches"/> で得た一致へジャンプする用途）。範囲は行内にクランプする。
    /// </summary>
    /// <returns>行インデックスが有効で選択できれば <c>true</c>。</returns>
    public bool SelectMatch(int lineIndex, int column, int length)
    {
        if (!TerminalSelectionSearchModel.TryCreateMatchRange(
            SelectionLines, lineIndex, column, length, out TerminalTextRange range))
        {
            return false;
        }
        SelectRange(range);
        return true;
    }

    public bool TrySelectNextMatch(string query, StringComparison comparison, bool forward, out bool wrapped)
    {
        if (!TerminalSelectionSearchModel.TryFindNext(
            SelectionLines, _selection, query, comparison, forward, out TerminalTextRange match, out wrapped))
        {
            return false;
        }
        SelectRange(match);
        return true;
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

        TerminalLineLayout line = _lines[lineIndex];
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
            HorizontalOffset,
            VerticalOffset);
    }

    public void ScrollToLineEnd()
    {
        SetVerticalOffset(Math.Max(0, ExtentHeight - ViewportHeight));
    }

    public void LineUp()
    {
        MoveVertical(TerminalScrollDelta.LineBackward);
    }

    public void LineDown()
    {
        MoveVertical(TerminalScrollDelta.LineForward);
    }

    public void LineLeft()
    {
        MoveHorizontal(TerminalScrollDelta.LineBackward);
    }

    public void LineRight()
    {
        MoveHorizontal(TerminalScrollDelta.LineForward);
    }

    public void PageUp()
    {
        MoveVertical(TerminalScrollDelta.PageBackward);
    }

    public void PageDown()
    {
        MoveVertical(TerminalScrollDelta.PageForward);
    }

    public void PageLeft()
    {
        MoveHorizontal(TerminalScrollDelta.PageBackward);
    }

    public void PageRight()
    {
        MoveHorizontal(TerminalScrollDelta.PageForward);
    }

    public void MouseWheelUp()
    {
        MoveVertical(TerminalScrollDelta.WheelBackward, SystemParameters.WheelScrollLines);
    }

    public void MouseWheelDown()
    {
        MoveVertical(TerminalScrollDelta.WheelForward, SystemParameters.WheelScrollLines);
    }

    public void MouseWheelLeft()
    {
        MoveHorizontal(TerminalScrollDelta.WheelBackward, SystemParameters.WheelScrollLines);
    }

    public void MouseWheelRight()
    {
        MoveHorizontal(TerminalScrollDelta.WheelForward, SystemParameters.WheelScrollLines);
    }

    private void MoveHorizontal(TerminalScrollDelta delta, int wheelLines = 0)
    {
        EnsureMetrics();
        if (_scrollState.MoveHorizontal(delta, _cellSize.Width, wheelLines))
        {
            ScrollOwner?.InvalidateScrollInfo();
            InvalidateVisual();
        }
    }

    private void MoveVertical(TerminalScrollDelta delta, int wheelLines = 0)
    {
        EnsureMetrics();
        if (_scrollState.MoveVertical(delta, _cellSize.Height, wheelLines))
        {
            ScrollOwner?.InvalidateScrollInfo();
            InvalidateVisual();
        }
    }

    public void SetHorizontalOffset(double offset)
    {
        if (!_scrollState.SetHorizontalOffset(offset))
        {
            return;
        }

        ScrollOwner?.InvalidateScrollInfo();
        InvalidateVisual();
    }

    public void SetVerticalOffset(double offset)
    {
        if (!_scrollState.SetVerticalOffset(offset))
        {
            return;
        }

        ScrollOwner?.InvalidateScrollInfo();
        InvalidateVisual();
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        if (!ReferenceEquals(visual, this))
        {
            return rectangle;
        }

        if (rectangle.IsEmpty)
        {
            return rectangle;
        }

        TerminalScrollMakeVisibleResult result = _scrollState.MakeVisible(new(
            rectangle.Left, rectangle.Top, rectangle.Width, rectangle.Height));
        if (result.HorizontalOffsetChanged || result.VerticalOffsetChanged)
        {
            ScrollOwner?.InvalidateScrollInfo();
            InvalidateVisual();
        }

        TerminalScrollRectangle visible = result.VisibleRectangle;
        return result.HasVisibleIntersection
            ? new Rect(visible.Left, visible.Top, visible.Width, visible.Height)
            : Rect.Empty;
    }

    protected override Size MeasureOverride(Size constraint)
    {
        EnsureMetrics();
        Size viewportSize = ResolveViewportSize(constraint);
        UpdateViewport(viewportSize);
        return new Size(
            double.IsInfinity(constraint.Width) ? ExtentWidth : Math.Max(constraint.Width, _scrollState.ViewportFloorWidth),
            double.IsInfinity(constraint.Height) ? ExtentHeight : Math.Max(constraint.Height, _scrollState.ViewportFloorHeight));
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

        double contentLeft = padding.Left - HorizontalOffset;
        double contentTop = padding.Top - VerticalOffset;
        TerminalScrollLineWindow lineWindow = _scrollState.GetLineWindow(
            _lines.Count, _cellSize.Height, padding.Top);
        int firstVisibleLine = lineWindow.FirstVisibleLine;
        int lastVisibleLine = lineWindow.LastVisibleLine;

        // Drop layouts that have scrolled out of view (plus a one-screen margin on each side so
        // small scrolls reuse them) to keep the cache bounded to roughly the viewport.
        _lines.TrimOutsideWindow(lineWindow.CacheStartLine, lineWindow.CacheEndLine);

        TerminalTextRange? selection = NormalizeSelection(_selection);
        bool sawBlinkingContent = false;
        for (int lineIndex = firstVisibleLine; lineIndex <= lastVisibleLine; lineIndex++)
        {
            TerminalLineLayout line = _lines[lineIndex];
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

        TerminalLineLayout line = _lines[lineIndex];
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

        TerminalLineLayout line = _lines[lineIndex];
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

    private void DrawLineBackgrounds(DrawingContext drawingContext, TerminalLineLayout line, double top, double contentLeft)
    {
        foreach (TerminalLineSegmentLayout segment in line.Segments)
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
        TerminalLineLayout line,
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
    private LineDrawable BuildLineDrawable(TerminalLineLayout line)
    {
        var commands = new List<IDrawCommand>();
        try
        {
            foreach (TerminalLineSegmentLayout segment in line.Segments)
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
        catch
        {
            DisposableResourceOwner.RollBackBestEffort(commands.OfType<IDisposable>());
            throw;
        }
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
            TerminalScrollState.AreClose(_pixelsPerDip, pixelsPerDip) &&
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
        if (!_scrollState.SetViewport(size.Width, size.Height))
        {
            return;
        }

        // The extent height folds in the viewport's sub-row remainder, so recompute it whenever
        // the viewport size changes rather than only on snapshot/floor updates.
        UpdateScrollMetrics();
        ScrollOwner?.InvalidateScrollInfo();
    }

    private void UpdateScrollMetrics()
    {
        EnsureMetrics();
        Thickness padding = Padding;
        _scrollState.UpdateMetrics(new TerminalScrollContentMetrics(
            _maxCellLength,
            _lines.Count,
            _cellSize.Width,
            _cellSize.Height,
            padding.Left,
            padding.Top,
            padding.Right,
            padding.Bottom));
        if (!TerminalScrollState.AreClose(ExtentWidth, _lastReportedExtentWidth) ||
            !TerminalScrollState.AreClose(ExtentHeight, _lastReportedExtentHeight))
        {
            if (ScrollOwner is not null)
            {
                _lastReportedExtentWidth = ExtentWidth;
                _lastReportedExtentHeight = ExtentHeight;
                ScrollOwner.InvalidateScrollInfo();
            }
        }
    }

    private Size ResolveViewportSize(Size constraint)
    {
        (double width, double height) = _scrollState.ResolveViewport(constraint.Width, constraint.Height);
        return new Size(width, height);
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
        => TerminalSelectionSearchModel.ClampPosition(SelectionLines, position);

    private void SelectRange(TerminalTextRange range)
    {
        _blockSelectionMode = false;
        _selection = NormalizeSelection(range);
        BringSelectionIntoView();
        InvalidateVisual();
    }

    internal void SelectRange(
        TerminalTextRange range,
        bool blockSelection,
        double blockAnchorCellColumn,
        double blockCurrentCellColumn)
    {
        _blockSelectionMode = blockSelection;
        _blockAnchorCellColumn = blockAnchorCellColumn;
        _blockCurrentCellColumn = blockCurrentCellColumn;
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

        TerminalLineLayout line = _lines[range.Start.LineIndex];
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

        TerminalLineLayout line = _lines[lineIndex];
        if (!TerminalHyperlinkDetector.TryResolve(
                line.Text,
                line.TextCellMap,
                line.HyperlinkSegments,
                textIndex,
                out TerminalHyperlinkMatch match))
        {
            return false;
        }

        target = match.Target;
        startColumn = match.StartColumn;
        endColumn = match.EndColumn;
        return true;
    }

    private static TerminalTextRange? NormalizeSelection(TerminalTextRange? selection)
        => TerminalSelectionSearchModel.Normalize(selection);

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

    // A line's shaped, position-relative paint commands, reused across repaints. Disposing it
    // releases any unmanaged text resources held by its commands (e.g. TextLine).
    private sealed class LineDrawable : IDisposable
    {
        private IDrawCommand[] _commands;

        public LineDrawable(IDrawCommand[] commands)
        {
            _commands = commands;
        }

        public IDrawCommand[] Commands => _commands;

        public void Dispose()
        {
            IDrawCommand[] commands = _commands;
            _commands = [];
            DisposableResourceOwner.DisposeAllBestEffort(commands.OfType<IDisposable>());
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

    private sealed class SelectionLineList(TerminalLineRenderCache<LineDrawable> lines) : IReadOnlyList<TerminalSelectionLine>
    {
        public int Count => lines.Count;

        public TerminalSelectionLine this[int index]
        {
            get
            {
                TerminalLineLayout line = lines[index];
                return new TerminalSelectionLine(line.Text, line.TextCellMap);
            }
        }

        public IEnumerator<TerminalSelectionLine> GetEnumerator()
        {
            for (int index = 0; index < Count; index++)
            {
                yield return this[index];
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
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
