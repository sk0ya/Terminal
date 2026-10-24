using System.Runtime.CompilerServices;
using System.Windows.Media;

using Terminal.Buffer;

namespace Terminal.Tests;

public sealed class TerminalCellLayoutTests
{
    [Fact]
    public void CellStaysSmallBecauseScrollbackHoldsMillionsOfThem()
    {
        // 10000 行×120 桁で 120 万マス。書式を WPF の Color のまま持っていた頃は 184 バイトで、1 本 200MB を超えた。
        Assert.Equal(16, Unsafe.SizeOf<TerminalStyle>());
        Assert.True(Unsafe.SizeOf<TerminalCell>() <= 40, $"TerminalCell is {Unsafe.SizeOf<TerminalCell>()} bytes");
    }

    [Fact]
    public void StyleRoundTripsEveryAttribute()
    {
        var underline = Color.FromArgb(0x80, 0x12, 0x34, 0x56);
        var style = new TerminalStyle(
            Colors.Red, Color.FromRgb(1, 2, 3), Bold: true, Dim: false, Italic: true, UnderlineStyle.Double,
            underline, Blink: false, Inverse: true, Invisible: false, Strikethrough: true, Overline: false, Protected: true);

        Assert.Equal(Colors.Red, style.Foreground);
        Assert.Equal(Color.FromRgb(1, 2, 3), style.Background);
        Assert.Equal(underline, style.UnderlineColor);
        Assert.Equal(UnderlineStyle.Double, style.UnderlineStyle);
        Assert.True(style.Bold);
        Assert.False(style.Dim);
        Assert.True(style.Italic);
        Assert.False(style.Blink);
        Assert.True(style.Inverse);
        Assert.False(style.Invisible);
        Assert.True(style.Strikethrough);
        Assert.False(style.Overline);
        Assert.True(style.Protected);
    }

    [Fact]
    public void ClearedColorIsEqualToNeverSetColor()
    {
        // 色を外した書式が既定と等しくないと、空白の判定（既定書式かどうか）や区間のまとめが崩れる。
        TerminalStyle cleared = TerminalStyle.Default with { Foreground = Colors.Black, Background = Colors.Black };
        cleared = cleared with { Foreground = null, Background = null };

        Assert.Null(cleared.Foreground);
        Assert.Equal(TerminalStyle.Default, cleared);
    }

    [Fact]
    public void BlackIsDistinctFromNoColor()
    {
        // ARGB が 0 の色（透明な黒）も「色なし」とは別物として残る。
        TerminalStyle transparent = TerminalStyle.Default with { Foreground = Color.FromArgb(0, 0, 0, 0) };

        Assert.Equal(Color.FromArgb(0, 0, 0, 0), transparent.Foreground);
        Assert.NotEqual(TerminalStyle.Default, transparent);
    }

    [Fact]
    public void CellKeepsWidthAndContinuation()
    {
        var cell = new TerminalCell("界", TerminalStyle.Default, Hyperlink: null, IsContinuation: false, Width: 2);
        TerminalCell continuation = cell with { Text = string.Empty, IsContinuation = true, Width = 0 };

        Assert.Equal(2, cell.Width);
        Assert.False(cell.IsContinuation);
        Assert.Equal(0, continuation.Width);
        Assert.True(continuation.IsContinuation);
    }
}
