using System.Windows.Media;

namespace Terminal.Buffer;

internal enum TerminalLineSize
{
    SingleWidth,
    DoubleWidth,
    DoubleHeightTop,
    DoubleHeightBottom
}

internal sealed class TerminalLine
{
    public TerminalLine(int columns, TerminalStyle blankStyle)
    {
        Cells = new TerminalCell[columns];
        for (int index = 0; index < columns; index++)
        {
            Cells[index] = TerminalCell.CreateBlank(blankStyle);
        }
    }

    public TerminalCell[] Cells { get; }
    public bool IsWrapped { get; set; }
    public TerminalLineSize LineSize { get; set; }
}

internal readonly record struct TerminalCell(
    string Text,
    TerminalStyle Style,
    string? Hyperlink,
    bool IsContinuation,
    int Width)
{
    public static TerminalCell CreateBlank(TerminalStyle style) =>
        new(" ", style, Hyperlink: null, IsContinuation: false, Width: 1);
}

internal readonly record struct TerminalStyle(
    Color? Foreground,
    Color? Background,
    bool Bold,
    bool Dim,
    bool Italic,
    UnderlineStyle UnderlineStyle,
    Color? UnderlineColor,
    bool Blink,
    bool Inverse,
    bool Invisible,
    bool Strikethrough,
    bool Overline,
    bool Protected)
{
    public static readonly TerminalStyle Default = new(
        null, null, false, false, false, UnderlineStyle.None, null, false, false, false, false, false, false);
}
