using System.Windows;

namespace Terminal.Tests;

public sealed class TerminalViewportSizingTests
{
    [Fact]
    public void ResolveViewportSizeSubtractsBorderAndPaddingWithoutScrollViewerViewport()
    {
        Size viewport = TerminalViewportSizing.ResolveViewportSize(
            new Size(800, 600),
            new Thickness(1),
            new Thickness(12));

        Assert.Equal(774, viewport.Width);
        Assert.Equal(574, viewport.Height);
    }

    [Fact]
    public void ResolveViewportSizePrefersMeasuredScrollViewerViewport()
    {
        Size viewport = TerminalViewportSizing.ResolveViewportSize(
            new Size(800, 600),
            new Thickness(1),
            new Thickness(12),
            new Size(752, 548));

        Assert.Equal(752, viewport.Width);
        Assert.Equal(548, viewport.Height);
    }

    [Fact]
    public void CalculateCellCountUsesViewportExtentAndFallsBackForInvalidValues()
    {
        Assert.Equal<short>(36, TerminalViewportSizing.CalculateCellCount(576, 16, fallback: 30, min: 10, max: 300));
        Assert.Equal<short>(30, TerminalViewportSizing.CalculateCellCount(0, 16, fallback: 30, min: 10, max: 300));
        Assert.Equal<short>(120, TerminalViewportSizing.CalculateCellCount(double.NaN, 8, fallback: 120, min: 20, max: 500));
    }
}
