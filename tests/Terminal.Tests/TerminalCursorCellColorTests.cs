using System.Windows.Media;

using Terminal.Rendering;

namespace Terminal.Tests;

public sealed class TerminalCursorCellColorTests
{
    private static readonly Color Fill = Color.FromRgb(0xE3, 0xE3, 0xE3);
    private static readonly Color Dark = Color.FromRgb(0x0E, 0x0C, 0x0A);
    private static readonly Color Light = Color.FromRgb(0xE8, 0xE0, 0xD2);

    [Fact]
    public void NormalCellIsInverted()
    {
        // Light text on a dark cell: the glyph is drawn in the cell's dark background.
        Assert.Equal(Dark, TerminalSurfaceControl.PickCursorTextColor(Fill, cellBackground: Dark, cellForeground: Light));
    }

    [Fact]
    public void AlreadyInvertedCellKeepsItsForeground()
    {
        // An app's own reverse-video cursor (Claude Code) has a light background close to the fill;
        // inverting again would draw light on light, so its dark foreground is used.
        Assert.Equal(Dark, TerminalSurfaceControl.PickCursorTextColor(Fill, cellBackground: Light, cellForeground: Dark));
    }
}
