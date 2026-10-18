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
    public List<TerminalImage> Images { get; } = [];
    public bool IsWrapped { get; set; }
    public TerminalLineSize LineSize { get; set; }

    /// <summary>この行に最初に文字が書かれた時刻（UTC の Ticks）。0 は不明（まだ何も書かれていない・古い再生）。
    /// 折り返し直しでは論理行の先頭の行の値を引き継ぐ。</summary>
    public long WrittenAtUtcTicks { get; set; }
}

internal readonly record struct TerminalCell(
    string Text,
    TerminalStyle Style,
    TerminalHyperlink? Hyperlink,
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
