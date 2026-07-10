using Terminal.Settings;

namespace Terminal;

internal enum TerminalPopupEdge
{
    Top,
    Bottom,
    Left,
    Right
}

internal sealed record TerminalWindowLayout(
    string Placement,
    bool IsTop,
    bool IsHorizontal,
    TerminalPopupEdge PopupEdge,
    double HorizontalOffset,
    double ProfilePickerVerticalOffset,
    double AppMenuVerticalOffset)
{
    internal static TerminalWindowLayout Resolve(string? rawPlacement)
    {
        string placement = TerminalTabStripPlacementCatalog.Normalize(rawPlacement);
        return placement switch
        {
            TerminalTabStripPlacementCatalog.Bottom => new(
                placement, false, true, TerminalPopupEdge.Top, -8, -4, -4),
            TerminalTabStripPlacementCatalog.Left => new(
                placement, false, false, TerminalPopupEdge.Right, 4, -8, -6),
            TerminalTabStripPlacementCatalog.Right => new(
                placement, false, false, TerminalPopupEdge.Left, -4, -8, -6),
            _ => new(
                placement, true, true, TerminalPopupEdge.Bottom, -8, 4, 4)
        };
    }
}
