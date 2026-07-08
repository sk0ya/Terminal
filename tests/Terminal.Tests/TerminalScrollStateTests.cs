using Terminal.Rendering;

namespace Terminal.Tests;

public sealed class TerminalScrollStateTests
{
    [Fact]
    public void MetricsApplyFloorPaddingAndSubCellViewportRemainder()
    {
        var state = new TerminalScrollState();
        state.SetViewportFloor(80, 50);
        state.SetViewport(45, 45);

        state.UpdateMetrics(Content(maxCells: 10, lines: 8, cellWidth: 8, cellHeight: 10, padding: 2));

        Assert.Equal(84, state.ExtentWidth);
        Assert.Equal(85, state.ExtentHeight);
        Assert.Equal(45, state.ViewportWidth);
        Assert.Equal(45, state.ViewportHeight);
    }

    [Fact]
    public void ShrinkingExtentClampsOffsetsWhileGrowingPreservesThem()
    {
        var state = StateWithContent(20, 20, viewport: 50);
        state.SetHorizontalOffset(75);
        state.SetVerticalOffset(90);

        state.UpdateMetrics(Content(30, 30, 10, 10));
        Assert.Equal(75, state.HorizontalOffset);
        Assert.Equal(90, state.VerticalOffset);

        state.UpdateMetrics(Content(8, 7, 10, 10));
        Assert.Equal(30, state.HorizontalOffset);
        Assert.Equal(20, state.VerticalOffset);
    }

    [Fact]
    public void FloorAndViewportNormalizeInvalidValuesConsistently()
    {
        var state = new TerminalScrollState();

        Assert.False(state.SetViewportFloor(double.NaN, double.PositiveInfinity));
        Assert.Equal(0, state.ViewportFloorWidth);
        Assert.Equal(0, state.ViewportFloorHeight);

        Assert.False(state.SetViewport(double.PositiveInfinity, -5));
        Assert.Equal(0, state.ViewportWidth);
        Assert.Equal(0, state.ViewportHeight);
    }

    [Fact]
    public void OffsetClampingPreservesNaNAndClampsInfinities()
    {
        var state = StateWithContent(20, 20, viewport: 50);

        Assert.True(state.SetHorizontalOffset(double.NaN));
        Assert.True(double.IsNaN(state.HorizontalOffset));
        Assert.True(state.SetVerticalOffset(double.PositiveInfinity));
        Assert.Equal(150, state.VerticalOffset);
        Assert.True(state.SetVerticalOffset(double.NegativeInfinity));
        Assert.Equal(0, state.VerticalOffset);
    }

    [Fact]
    public void MakeVisibleMovesBothAxesAndReturnsViewportIntersection()
    {
        var state = StateWithContent(30, 30, viewport: 50);

        TerminalScrollMakeVisibleResult result = state.MakeVisible(new(80, 90, 20, 30));

        Assert.Equal(50, state.HorizontalOffset);
        Assert.Equal(70, state.VerticalOffset);
        Assert.True(result.HorizontalOffsetChanged);
        Assert.True(result.VerticalOffsetChanged);
        Assert.True(result.HasVisibleIntersection);
        Assert.Equal(new TerminalScrollRectangle(80, 90, 20, 30), result.VisibleRectangle);
    }

    [Fact]
    public void LinePageAndWheelDeltasUseCellAndViewportSizes()
    {
        var state = StateWithContent(50, 50, viewport: 100);
        state.SetVerticalOffset(200);
        state.MoveVertical(TerminalScrollDelta.LineBackward, 10);
        Assert.Equal(190, state.VerticalOffset);
        state.MoveVertical(TerminalScrollDelta.PageForward, 10);
        Assert.Equal(290, state.VerticalOffset);
        state.MoveVertical(TerminalScrollDelta.WheelBackward, 10, wheelLines: 3);
        Assert.Equal(260, state.VerticalOffset);

        state.SetHorizontalOffset(200);
        state.MoveHorizontal(TerminalScrollDelta.LineForward, 8);
        state.MoveHorizontal(TerminalScrollDelta.PageBackward, 8);
        state.MoveHorizontal(TerminalScrollDelta.WheelForward, 8, wheelLines: 3);
        Assert.Equal(132, state.HorizontalOffset);
    }

    [Fact]
    public void VisibleAndCacheWindowAccountForPaddingAndOneScreenMargin()
    {
        var state = StateWithContent(20, 100, viewport: 35);
        state.SetVerticalOffset(27);

        TerminalScrollLineWindow window = state.GetLineWindow(100, cellHeight: 10, paddingTop: 7);

        Assert.Equal(2, window.FirstVisibleLine);
        Assert.Equal(6, window.LastVisibleLine);
        Assert.Equal(-3, window.CacheStartLine);
        Assert.Equal(11, window.CacheEndLine);
    }

    [Fact]
    public void EmptyContentHasAnEmptyLineWindow()
    {
        var state = new TerminalScrollState();
        state.SetViewport(100, 100);

        Assert.Equal(new TerminalScrollLineWindow(0, -1, 0, -1), state.GetLineWindow(0, 10, 0));
    }

    private static TerminalScrollState StateWithContent(int cells, int lines, double viewport)
    {
        var state = new TerminalScrollState();
        state.SetViewport(viewport, viewport);
        state.UpdateMetrics(Content(cells, lines, 10, 10));
        return state;
    }

    private static TerminalScrollContentMetrics Content(
        int maxCells,
        int lines,
        double cellWidth,
        double cellHeight,
        double padding = 0) =>
        new(maxCells, lines, cellWidth, cellHeight, padding, padding, padding, padding);
}
