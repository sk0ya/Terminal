namespace Terminal.Rendering;

internal sealed class TerminalScrollState
{
    private const double CloseTolerance = 0.01;

    public double ExtentWidth { get; private set; }
    public double ExtentHeight { get; private set; }
    public double ViewportWidth { get; private set; }
    public double ViewportHeight { get; private set; }
    public double ViewportFloorWidth { get; private set; }
    public double ViewportFloorHeight { get; private set; }
    public double HorizontalOffset { get; private set; }
    public double VerticalOffset { get; private set; }

    public bool SetViewportFloor(double width, double height)
    {
        double nextWidth = NormalizeFinitePositive(width);
        double nextHeight = NormalizeFinitePositive(height);
        if (AreClose(ViewportFloorWidth, nextWidth) && AreClose(ViewportFloorHeight, nextHeight))
        {
            return false;
        }

        ViewportFloorWidth = nextWidth;
        ViewportFloorHeight = nextHeight;
        return true;
    }

    public bool SetViewport(double width, double height)
    {
        double nextWidth = NormalizeViewport(width);
        double nextHeight = NormalizeViewport(height);
        if (AreClose(ViewportWidth, nextWidth) && AreClose(ViewportHeight, nextHeight))
        {
            return false;
        }

        ViewportWidth = nextWidth;
        ViewportHeight = nextHeight;
        return true;
    }

    public bool UpdateMetrics(TerminalScrollContentMetrics content)
    {
        double previousWidth = ExtentWidth;
        double previousHeight = ExtentHeight;
        ExtentWidth = Math.Max(
            ViewportFloorWidth,
            content.PaddingLeft + content.PaddingRight + (content.MaximumCellLength * content.CellWidth));
        ExtentHeight = Math.Max(
            ViewportFloorHeight,
            content.PaddingTop + content.PaddingBottom + (content.LineCount * content.CellHeight) +
            ComputeSubCellViewportRemainder(content.CellHeight, content.PaddingTop, content.PaddingBottom));
        CoerceOffsets();
        return !AreClose(previousWidth, ExtentWidth) || !AreClose(previousHeight, ExtentHeight);
    }

    public bool SetHorizontalOffset(double offset) =>
        SetOffset(offset, Math.Max(0, ExtentWidth - ViewportWidth), horizontal: true);

    public bool SetVerticalOffset(double offset) =>
        SetOffset(offset, Math.Max(0, ExtentHeight - ViewportHeight), horizontal: false);

    public bool MoveHorizontal(TerminalScrollDelta kind, double cellSize, int wheelLines = 0) =>
        SetHorizontalOffset(HorizontalOffset + ResolveDelta(kind, cellSize, ViewportWidth, wheelLines));

    public bool MoveVertical(TerminalScrollDelta kind, double cellSize, int wheelLines = 0) =>
        SetVerticalOffset(VerticalOffset + ResolveDelta(kind, cellSize, ViewportHeight, wheelLines));

    public TerminalScrollMakeVisibleResult MakeVisible(TerminalScrollRectangle target)
    {
        double horizontalTarget = HorizontalOffset;
        if (target.Left < HorizontalOffset)
        {
            horizontalTarget = target.Left;
        }
        else if (target.Right > HorizontalOffset + ViewportWidth)
        {
            horizontalTarget = target.Right - ViewportWidth;
        }

        double verticalTarget = VerticalOffset;
        if (target.Top < VerticalOffset)
        {
            verticalTarget = target.Top;
        }
        else if (target.Bottom > VerticalOffset + ViewportHeight)
        {
            verticalTarget = target.Bottom - ViewportHeight;
        }

        bool horizontalChanged = SetHorizontalOffset(horizontalTarget);
        bool verticalChanged = SetVerticalOffset(verticalTarget);
        (TerminalScrollRectangle intersection, bool hasIntersection) = target.Intersect(new(
            HorizontalOffset, VerticalOffset, ViewportWidth, ViewportHeight));
        return new(intersection, hasIntersection, horizontalChanged, verticalChanged);
    }

    public TerminalScrollLineWindow GetLineWindow(
        int lineCount,
        double cellHeight,
        double paddingTop)
    {
        if (lineCount <= 0 || !double.IsFinite(cellHeight) || cellHeight <= 0)
        {
            return new(0, -1, 0, -1);
        }

        int first = Math.Max(0, (int)Math.Floor(Math.Max(0, VerticalOffset - paddingTop) / cellHeight));
        int last = Math.Min(
            lineCount - 1,
            (int)Math.Ceiling(Math.Max(0, (VerticalOffset - paddingTop) + ViewportHeight) / cellHeight));
        int span = Math.Max(0, last - first + 1);
        return new(first, last, first - span, last + span);
    }

    public (double Width, double Height) ResolveViewport(double width, double height) => (
        double.IsInfinity(width) ? ViewportFloorWidth : Math.Max(width, ViewportFloorWidth),
        double.IsInfinity(height) ? ViewportFloorHeight : Math.Max(height, ViewportFloorHeight));

    internal static bool AreClose(double left, double right) => Math.Abs(left - right) < CloseTolerance;

    private static double NormalizeFinitePositive(double value) =>
        double.IsFinite(value) && value > 0 ? value : 0;

    private static double NormalizeViewport(double value) =>
        double.IsInfinity(value) ? 0 : Math.Max(0, value);

    private bool SetOffset(double value, double maximum, bool horizontal)
    {
        double next = Math.Clamp(value, 0, maximum);
        double current = horizontal ? HorizontalOffset : VerticalOffset;
        if (AreClose(current, next))
        {
            return false;
        }

        if (horizontal)
        {
            HorizontalOffset = next;
        }
        else
        {
            VerticalOffset = next;
        }

        return true;
    }

    private void CoerceOffsets()
    {
        HorizontalOffset = Math.Clamp(HorizontalOffset, 0, Math.Max(0, ExtentWidth - ViewportWidth));
        VerticalOffset = Math.Clamp(VerticalOffset, 0, Math.Max(0, ExtentHeight - ViewportHeight));
    }

    private double ComputeSubCellViewportRemainder(double cellHeight, double paddingTop, double paddingBottom)
    {
        if (cellHeight <= 0)
        {
            return 0;
        }

        double contentHeight = ViewportHeight - paddingTop - paddingBottom;
        if (contentHeight <= 0)
        {
            return 0;
        }

        double rows = contentHeight / cellHeight;
        double fraction = rows - Math.Floor(rows);
        return fraction < 1e-6 || fraction > 1 - 1e-6 ? 0 : fraction * cellHeight;
    }

    private static double ResolveDelta(TerminalScrollDelta kind, double cellSize, double viewport, int wheelLines) => kind switch
    {
        TerminalScrollDelta.LineBackward => -cellSize,
        TerminalScrollDelta.LineForward => cellSize,
        TerminalScrollDelta.PageBackward => -viewport,
        TerminalScrollDelta.PageForward => viewport,
        TerminalScrollDelta.WheelBackward => -(wheelLines * cellSize),
        TerminalScrollDelta.WheelForward => wheelLines * cellSize,
        _ => 0
    };
}

internal enum TerminalScrollDelta
{
    LineBackward,
    LineForward,
    PageBackward,
    PageForward,
    WheelBackward,
    WheelForward
}

internal readonly record struct TerminalScrollContentMetrics(
    int MaximumCellLength,
    int LineCount,
    double CellWidth,
    double CellHeight,
    double PaddingLeft,
    double PaddingTop,
    double PaddingRight,
    double PaddingBottom);

internal readonly record struct TerminalScrollRectangle(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;

    public (TerminalScrollRectangle Rectangle, bool HasIntersection) Intersect(TerminalScrollRectangle other)
    {
        double left = Math.Max(Left, other.Left);
        double top = Math.Max(Top, other.Top);
        double right = Math.Min(Right, other.Right);
        double bottom = Math.Min(Bottom, other.Bottom);
        return right < left || bottom < top
            ? (new(0, 0, 0, 0), false)
            : (new(left, top, right - left, bottom - top), true);
    }
}

internal readonly record struct TerminalScrollMakeVisibleResult(
    TerminalScrollRectangle VisibleRectangle,
    bool HasVisibleIntersection,
    bool HorizontalOffsetChanged,
    bool VerticalOffsetChanged);

internal readonly record struct TerminalScrollLineWindow(
    int FirstVisibleLine,
    int LastVisibleLine,
    int CacheStartLine,
    int CacheEndLine);
