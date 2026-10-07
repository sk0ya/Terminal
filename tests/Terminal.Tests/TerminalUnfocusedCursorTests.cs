using Terminal.Buffer;
using Terminal.Tabs;

namespace Terminal.Tests;

public sealed class TerminalUnfocusedCursorTests
{
    [Theory]
    [InlineData(TerminalCursorShape.Bar, true, TerminalCursorShape.Bar)]
    [InlineData(TerminalCursorShape.Underline, true, TerminalCursorShape.Underline)]
    [InlineData(TerminalCursorShape.Bar, false, TerminalCursorShape.Block)]
    [InlineData(TerminalCursorShape.Underline, false, TerminalCursorShape.Block)]
    [InlineData(TerminalCursorShape.Block, false, TerminalCursorShape.Block)]
    internal void UnfocusedCursorCoversTheWholeCell(TerminalCursorShape shape, bool focused, TerminalCursorShape expected)
    {
        Assert.Equal(expected, TerminalTabView.ResolveOverlayCursorShape(shape, focused));
    }
}
