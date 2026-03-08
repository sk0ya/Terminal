using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace ConPtyTerminal;

public sealed class TerminalSurfaceControl : Control, IScrollInfo
{
    private static readonly Brush SelectionBrush = CreateFrozenBrush(Color.FromArgb(0x66, 0xE1, 0x9A, 0x4A));
    private static readonly Brush DefaultBackgroundBrush = CreateFrozenBrush(Color.FromRgb(0x0E, 0x0C, 0x0A));
    private static readonly Brush DefaultForegroundBrush = CreateFrozenBrush(Color.FromRgb(0xE8, 0xE0, 0xD2));

    private readonly Dictionary<Color, SolidColorBrush> _brushCache = [];
    private readonly List<LineLayout> _lines = [];
    private Typeface? _typeface;
    private Size _cellSize = new(8, 16);
    private double _pixelsPerDip = 1.0;
    private bool _metricsDirty = true;
    private int _maxCellLength;
    private TerminalTextRange? _selection;
    private TerminalTextPosition? _selectionAnchor;
    private Point? _selectionAnchorPoint;
    private bool _selectionDragStarted;
    private double _extentWidth;
    private double _extentHeight;
    private double _viewportWidth;
    private double _viewportHeight;
    private double _horizontalOffset;
    private double _verticalOffset;

    public event EventHandler<TerminalHyperlinkActivatedEventArgs>? HyperlinkActivated;

    static TerminalSurfaceControl()
    {
        FocusableProperty.OverrideMetadata(typeof(TerminalSurfaceControl), new FrameworkPropertyMetadata(true));
    }

    public TerminalSurfaceControl()
    {
        SnapsToDevicePixels = true;
        FocusVisualStyle = null;
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

    public int LineCount => _lines.Count;

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
        _lines.Clear();
        _maxCellLength = 0;

        foreach (AnsiTerminalBuffer.TerminalRenderLineSnapshot line in snapshot.Lines)
        {
            _lines.Add(CreateLineLayout(line));
            _maxCellLength = Math.Max(_maxCellLength, line.CellLength);
        }

        CoerceSelection();
        UpdateScrollMetrics();
        InvalidateVisual();
    }

    public void ClearSelection()
    {
        if (!_selection.HasValue)
        {
            return;
        }

        _selection = null;
        InvalidateVisual();
    }

    public string GetSelectedText()
    {
        TerminalTextRange? selection = NormalizeSelection(_selection);
        if (!selection.HasValue)
        {
            return string.Empty;
        }

        TerminalTextRange range = selection.Value;
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

    public int CountMatches(string query, StringComparison comparison)
    {
        if (string.IsNullOrEmpty(query))
        {
            return 0;
        }

        int count = 0;
        foreach (LineLayout line in _lines)
        {
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

    public bool TryGetTextPositionFromPoint(Point point, out int lineIndex, out int textIndex)
    {
        EnsureMetrics();
        Thickness padding = Padding;
        double x = Math.Max(0, point.X - padding.Left);
        double y = Math.Max(0, point.Y - padding.Top);
        lineIndex = Math.Clamp((int)(y / _cellSize.Height), 0, Math.Max(0, _lines.Count - 1));

        if (_lines.Count == 0)
        {
            textIndex = 0;
            return false;
        }

        LineLayout line = _lines[lineIndex];
        if (line.Map.Length == 0)
        {
            textIndex = 0;
            return true;
        }

        double columnPosition = x / _cellSize.Width;
        textIndex = GetTextIndexForColumnHit(line, columnPosition);
        return true;
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
        UpdateViewport(constraint);
        return new Size(
            double.IsInfinity(constraint.Width) ? ExtentWidth : constraint.Width,
            double.IsInfinity(constraint.Height) ? ExtentHeight : constraint.Height);
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
            return;
        }

        double contentLeft = padding.Left - _horizontalOffset;
        double contentTop = padding.Top - _verticalOffset;
        int firstVisibleLine = Math.Max(0, (int)Math.Floor(Math.Max(0, _verticalOffset - padding.Top) / _cellSize.Height));
        int lastVisibleLine = Math.Min(
            _lines.Count - 1,
            (int)Math.Ceiling(Math.Max(0, (_verticalOffset - padding.Top) + _viewportHeight) / _cellSize.Height));

        TerminalTextRange? selection = NormalizeSelection(_selection);
        for (int lineIndex = firstVisibleLine; lineIndex <= lastVisibleLine; lineIndex++)
        {
            LineLayout line = _lines[lineIndex];
            double top = contentTop + (lineIndex * _cellSize.Height);
            DrawLineBackgrounds(drawingContext, line, top, contentLeft);
            DrawSelection(drawingContext, selection, lineIndex, line, top, contentLeft);
            DrawLineText(drawingContext, line, top, contentLeft);
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        Focus();
        if (!TryCreateTextPosition(e.GetPosition(this), out TerminalTextPosition position))
        {
            return;
        }

        _selectionAnchor = position;
        _selectionAnchorPoint = e.GetPosition(this);
        _selectionDragStarted = false;
        _selection = new TerminalTextRange(position, position);
        CaptureMouse();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (!_selectionAnchor.HasValue || e.LeftButton != MouseButtonState.Pressed)
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

        if (!_selectionDragStarted &&
            TryCreateTextPosition(e.GetPosition(this), out TerminalTextPosition position) &&
            TryGetHyperlink(position.LineIndex, position.TextIndex, out Uri? navigateUri) &&
            navigateUri is not null)
        {
            ClearSelection();
            HyperlinkActivated?.Invoke(this, new TerminalHyperlinkActivatedEventArgs(navigateUri));
        }
        else
        {
            TerminalTextRange? normalized = NormalizeSelection(_selection);
            if (!normalized.HasValue || normalized.Value.IsEmpty)
            {
                ClearSelection();
            }
        }

        _selectionAnchor = null;
        _selectionAnchorPoint = null;
        _selectionDragStarted = false;
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _selectionAnchor = null;
        _selectionAnchorPoint = null;
        _selectionDragStarted = false;
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
            ? GetCellColumnForTextIndex(line, range.Start.TextIndex, preferTrailingEdge: false)
            : 0;
        int endColumn = lineIndex == range.End.LineIndex
            ? GetCellColumnForTextIndex(line, range.End.TextIndex, preferTrailingEdge: true)
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
        drawingContext.DrawRectangle(SelectionBrush, null, rect);
    }

    private void DrawLineText(DrawingContext drawingContext, LineLayout line, double top, double contentLeft)
    {
        foreach (SegmentLayout segment in line.Segments)
        {
            if (string.IsNullOrEmpty(segment.Snapshot.Text))
            {
                continue;
            }

            double left = contentLeft + (segment.StartCell * _cellSize.Width);
            var text = new FormattedText(
                segment.Snapshot.Text,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                _typeface!,
                FontSize,
                GetBrush(segment.Snapshot.Foreground),
                _pixelsPerDip);

            text.SetFontWeight(segment.Snapshot.Bold ? FontWeights.SemiBold : FontWeights.Regular);
            if (segment.Snapshot.Underline)
            {
                text.SetTextDecorations(TextDecorations.Underline);
            }

            drawingContext.DrawText(text, new Point(left, top));
        }
    }

    private void EnsureMetrics()
    {
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (!_metricsDirty &&
            DoubleUtil.AreClose(_pixelsPerDip, pixelsPerDip) &&
            _typeface is not null)
        {
            return;
        }

        _pixelsPerDip = pixelsPerDip;
        _typeface = new Typeface(FontFamily, FontStyle, FontWeight, FontStretch);
        var text = new FormattedText(
            "W",
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            _typeface,
            FontSize,
            Foreground ?? DefaultForegroundBrush,
            _pixelsPerDip);
        _cellSize = new Size(
            Math.Max(1.0, Math.Ceiling(text.WidthIncludingTrailingWhitespace)),
            Math.Max(1.0, Math.Ceiling(text.Height)));
        _metricsDirty = false;
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
        CoerceOffsets();
        ScrollOwner?.InvalidateScrollInfo();
    }

    private void UpdateScrollMetrics()
    {
        EnsureMetrics();
        Thickness padding = Padding;
        _extentWidth = padding.Left + padding.Right + (_maxCellLength * _cellSize.Width);
        _extentHeight = padding.Top + padding.Bottom + (_lines.Count * _cellSize.Height);
        CoerceOffsets();
        ScrollOwner?.InvalidateScrollInfo();
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
        int startColumn = GetCellColumnForTextIndex(line, range.Start.TextIndex, preferTrailingEdge: false);
        int endColumn = range.End.LineIndex == range.Start.LineIndex
            ? GetCellColumnForTextIndex(line, range.End.TextIndex, preferTrailingEdge: true)
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

    private bool TryGetHyperlink(int lineIndex, int textIndex, out Uri? navigateUri)
    {
        navigateUri = null;
        if (lineIndex < 0 || lineIndex >= _lines.Count)
        {
            return false;
        }

        LineLayout line = _lines[lineIndex];
        int column = GetCellColumnForTextIndex(line, textIndex, preferTrailingEdge: false);
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

            if (Uri.TryCreate(segment.Snapshot.Hyperlink, UriKind.Absolute, out navigateUri))
            {
                return true;
            }
        }

        return false;
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

    private static LineLayout CreateLineLayout(AnsiTerminalBuffer.TerminalRenderLineSnapshot line)
    {
        string text = string.Concat(line.Segments.Select(static segment => segment.Text));
        TextElementMapEntry[] map = BuildTextMap(text, line.CellLength);
        var segments = new SegmentLayout[line.Segments.Length];
        int startCell = 0;
        for (int index = 0; index < line.Segments.Length; index++)
        {
            segments[index] = new SegmentLayout(startCell, line.Segments[index]);
            startCell += line.Segments[index].CellLength;
        }

        return new LineLayout(text, line.CellLength, segments, map);
    }

    private static TextElementMapEntry[] BuildTextMap(string text, int targetCellLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<TextElementMapEntry>();
        }

        int[] starts = StringInfo.ParseCombiningCharacters(text);
        var entries = new TextElementMapEntry[starts.Length];
        int totalCells = 0;
        for (int index = 0; index < starts.Length; index++)
        {
            int start = starts[index];
            int end = index + 1 < starts.Length ? starts[index + 1] : text.Length;
            string element = text[start..end];
            int cellLength = EstimateTextElementCellWidth(element);
            entries[index] = new TextElementMapEntry(start, end - start, totalCells, cellLength);
            totalCells += cellLength;
        }

        if (entries.Length > 0 && totalCells != targetCellLength)
        {
            TextElementMapEntry last = entries[^1];
            int adjustedCellLength = Math.Max(1, last.CellLength + (targetCellLength - totalCells));
            entries[^1] = last with { CellLength = adjustedCellLength };
        }

        return entries;
    }

    private static int EstimateTextElementCellWidth(string element)
    {
        bool hasVisibleRune = false;
        int maxWidth = 1;
        foreach (Rune rune in element.EnumerateRunes())
        {
            int width = GetDisplayWidth(rune);
            if (width <= 0)
            {
                continue;
            }

            hasVisibleRune = true;
            maxWidth = Math.Max(maxWidth, width);
        }

        return hasVisibleRune ? maxWidth : 1;
    }

    private int GetTextIndexForColumnHit(LineLayout line, double columnPosition)
    {
        if (line.Map.Length == 0)
        {
            return 0;
        }

        if (columnPosition <= 0)
        {
            return 0;
        }

        if (columnPosition >= line.CellLength)
        {
            return line.Text.Length;
        }

        foreach (TextElementMapEntry entry in line.Map)
        {
            if (columnPosition < entry.StartCell)
            {
                return entry.TextIndex;
            }

            double endCell = entry.StartCell + entry.CellLength;
            if (columnPosition <= endCell)
            {
                double midpoint = entry.StartCell + (entry.CellLength / 2.0);
                return columnPosition >= midpoint
                    ? entry.TextIndex + entry.TextLength
                    : entry.TextIndex;
            }
        }

        return line.Text.Length;
    }

    private static int GetCellColumnForTextIndex(LineLayout line, int textIndex, bool preferTrailingEdge)
    {
        if (textIndex <= 0 || line.Map.Length == 0)
        {
            return 0;
        }

        if (textIndex >= line.Text.Length)
        {
            return line.CellLength;
        }

        foreach (TextElementMapEntry entry in line.Map)
        {
            if (textIndex < entry.TextIndex)
            {
                return entry.StartCell;
            }

            int entryEnd = entry.TextIndex + entry.TextLength;
            if (textIndex < entryEnd)
            {
                return preferTrailingEdge ? entry.StartCell + entry.CellLength : entry.StartCell;
            }

            if (textIndex == entryEnd)
            {
                return entry.StartCell + entry.CellLength;
            }
        }

        return line.CellLength;
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

    private static int GetDisplayWidth(Rune rune)
    {
        UnicodeCategory category = Rune.GetUnicodeCategory(rune);
        if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format)
        {
            return 0;
        }

        int value = rune.Value;
        if (value is
            0x200D or
            0xFE0E or
            0xFE0F or
            >= 0x1F3FB and <= 0x1F3FF or
            >= 0xE0100 and <= 0xE01EF)
        {
            return 0;
        }

        if (rune.IsAscii)
        {
            return 1;
        }

        if (value is
            >= 0x1100 and <= 0x115F or
            >= 0x2329 and <= 0x232A or
            >= 0x2E80 and <= 0xA4CF or
            >= 0xAC00 and <= 0xD7A3 or
            >= 0xF900 and <= 0xFAFF or
            >= 0xFE10 and <= 0xFE19 or
            >= 0xFE30 and <= 0xFE6F or
            >= 0xFF00 and <= 0xFF60 or
            >= 0xFFE0 and <= 0xFFE6 or
            >= 0x1F1E6 and <= 0x1F1FF or
            >= 0x1F300 and <= 0x1FAFF or
            >= 0x20000 and <= 0x3FFFD)
        {
            return 2;
        }

        return 1;
    }

    private readonly record struct LineLayout(
        string Text,
        int CellLength,
        SegmentLayout[] Segments,
        TextElementMapEntry[] Map);

    private readonly record struct SegmentLayout(
        int StartCell,
        AnsiTerminalBuffer.TerminalRenderSegmentSnapshot Snapshot);

    private readonly record struct TextElementMapEntry(
        int TextIndex,
        int TextLength,
        int StartCell,
        int CellLength);

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

public sealed class TerminalHyperlinkActivatedEventArgs(Uri uri) : EventArgs
{
    public Uri Uri { get; } = uri;
}
