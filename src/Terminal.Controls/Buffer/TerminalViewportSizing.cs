using System.Windows;

namespace Terminal.Buffer;

internal static class TerminalViewportSizing
{
    public static Size ResolveViewportSize(
        Size actualSize,
        Thickness borderThickness,
        Thickness padding,
        Size? scrollViewerViewportSize = null)
    {
        double width = ResolveViewportExtent(
            actualSize.Width,
            borderThickness.Left,
            borderThickness.Right,
            padding.Left,
            padding.Right,
            scrollViewerViewportSize?.Width);
        double height = ResolveViewportExtent(
            actualSize.Height,
            borderThickness.Top,
            borderThickness.Bottom,
            padding.Top,
            padding.Bottom,
            scrollViewerViewportSize?.Height);

        return new Size(width, height);
    }

    public static Size ResolveScrollViewerViewportSize(
        Size viewportSize,
        Size actualSize,
        Thickness contentPadding)
    {
        return new Size(
            ResolveScrollViewerViewportExtent(
                viewportSize.Width,
                actualSize.Width,
                contentPadding.Left,
                contentPadding.Right),
            ResolveScrollViewerViewportExtent(
                viewportSize.Height,
                actualSize.Height,
                contentPadding.Top,
                contentPadding.Bottom));
    }

    public static Size ResolveScrollViewerViewportSize(
        Size viewportSize,
        Size actualSize,
        Size hostSize,
        Thickness contentPadding)
    {
        return new Size(
            ResolveScrollViewerViewportExtent(
                viewportSize.Width,
                actualSize.Width,
                hostSize.Width,
                contentPadding.Left,
                contentPadding.Right),
            ResolveScrollViewerViewportExtent(
                viewportSize.Height,
                actualSize.Height,
                hostSize.Height,
                contentPadding.Top,
                contentPadding.Bottom));
    }

    public static short CalculateCellCount(double viewportExtent, double cellExtent, short fallback, short min, short max)
    {
        if (!IsFinitePositive(viewportExtent) || !IsFinitePositive(cellExtent))
        {
            return fallback;
        }

        return (short)Math.Clamp((int)(viewportExtent / cellExtent), min, max);
    }

    private static double ResolveViewportExtent(
        double actualExtent,
        double borderStart,
        double borderEnd,
        double paddingStart,
        double paddingEnd,
        double? scrollViewerViewportExtent)
    {
        if (IsFinitePositive(scrollViewerViewportExtent))
        {
            return scrollViewerViewportExtent!.Value;
        }

        return Math.Max(0, actualExtent - borderStart - borderEnd - paddingStart - paddingEnd);
    }

    private static double ResolveScrollViewerViewportExtent(
        double viewportExtent,
        double actualExtent,
        double paddingStart,
        double paddingEnd)
    {
        if (IsFinitePositive(viewportExtent))
        {
            return Math.Max(0, viewportExtent - paddingStart - paddingEnd);
        }

        return Math.Max(0, actualExtent - paddingStart - paddingEnd);
    }

    private static double ResolveScrollViewerViewportExtent(
        double viewportExtent,
        double actualExtent,
        double hostExtent,
        double paddingStart,
        double paddingEnd)
    {
        double actualContentExtent = Math.Max(0, actualExtent - paddingStart - paddingEnd);
        double hostContentExtent = Math.Max(0, hostExtent - paddingStart - paddingEnd);
        if (!IsFinitePositive(viewportExtent))
        {
            return IsFinitePositive(actualContentExtent) ? actualContentExtent : hostContentExtent;
        }

        double viewportContentExtent = Math.Max(0, viewportExtent - paddingStart - paddingEnd);
        if (IsFinitePositive(hostContentExtent) && viewportContentExtent > hostContentExtent)
        {
            return hostContentExtent;
        }

        return viewportContentExtent;
    }

    private static bool IsFinitePositive(double? value)
    {
        return value is double scalar &&
            scalar > 0 &&
            !double.IsNaN(scalar) &&
            !double.IsInfinity(scalar);
    }
}
