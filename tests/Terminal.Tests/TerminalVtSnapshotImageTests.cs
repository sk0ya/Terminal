using Terminal.Buffer;

namespace Terminal.Tests;

/// <summary>
/// 画面に置かれた画像も VT スナップショットで戻ること。常駐セッションへ付け直すと Sixel の絵が消え、
/// 絵が占めていた空の行だけが残っていた（2026-11-06）。
/// </summary>
public sealed class TerminalVtSnapshotImageTests
{
    private const short Columns = 40;
    private const string Png =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR4nGNgYGAAAAAEAAH2FzhVAAAAAElFTkSuQmCC";

    private static string Snapshot(AnsiTerminalBuffer buffer) => buffer.CreateVtSnapshot([], null, null, null);

    private static AnsiTerminalBuffer Restore(string snapshot, short rows)
    {
        var restored = new AnsiTerminalBuffer(Columns, rows);
        restored.Process(snapshot);
        return restored;
    }

    private static TerminalImage[] ImagesOn(AnsiTerminalBuffer buffer, int line) =>
        buffer.AllLinesForTests.ElementAt(line).Images.ToArray();

    [Fact]
    public void SixelComesBackOnTheSameRowWithItsVirtualCellSizeAndWithoutMovingTheCursorAgain()
    {
        string bands = string.Concat(Enumerable.Repeat("#0" + new string('~', 20) + "-", 10));
        var buffer = new AnsiTerminalBuffer(Columns, 20);
        buffer.Process("PS> cmd\r\n  \u001bPq#0;2;100;0;0" + bands + "\u001b\\after\r\nPS> ");

        AnsiTerminalBuffer restored = Restore(Snapshot(buffer), 20);

        TerminalImage want = Assert.Single(ImagesOn(buffer, 1));
        TerminalImage got = Assert.Single(ImagesOn(restored, 1));
        Assert.Equal("sixel", got.Protocol);
        Assert.Equal(TerminalImageDataKind.Bgra32, got.DataKind);
        Assert.Equal(want.Column, got.Column);
        Assert.Equal(want.PixelWidth, got.PixelWidth);
        Assert.Equal(want.PixelHeight, got.PixelHeight);
        Assert.Equal(want.ScaledWidthCells, got.ScaledWidthCells);
        Assert.Equal(want.ScaledHeightCells, got.ScaledHeightCells);
        Assert.Equal(want.Data, got.Data);
        // 文字・カーソルは今までどおり（再生で Sixel を送り直すと、ここでもう一度 6 行進んでずれる）。
        Assert.Equal(buffer.CreatePlainTextSnapshot(), restored.CreatePlainTextSnapshot());
        Assert.Equal(buffer.CursorRow, restored.CursorRow);
        Assert.Equal(buffer.CursorColumn, restored.CursorColumn);
        Assert.Equal(Snapshot(buffer), Snapshot(restored));
    }

    [Fact]
    public void ImagesInTheScrollbackAndEncodedImagesComeBackToo()
    {
        var buffer = new AnsiTerminalBuffer(Columns, 8);
        buffer.Process("top\r\n\u001b]1337;File=inline=1;width=3;height=2:" + Png + "\u0007pic\r\n");
        for (int index = 0; index < 12; index++)
        {
            buffer.Process($"line {index}\r\n");
        }

        Assert.True(buffer.ScrollbackLineCount > 2);
        AnsiTerminalBuffer restored = Restore(Snapshot(buffer), 8);

        TerminalImage want = Assert.Single(ImagesOn(buffer, 1));
        TerminalImage got = Assert.Single(ImagesOn(restored, 1));
        Assert.Equal("iterm2", got.Protocol);
        Assert.Equal(TerminalImageDataKind.Encoded, got.DataKind);
        Assert.Equal(want.MimeType, got.MimeType);
        Assert.Equal(want.WidthCells, got.WidthCells);
        Assert.Equal(want.HeightCells, got.HeightCells);
        Assert.Equal(want.Data, got.Data);
        Assert.Equal(Snapshot(buffer), Snapshot(restored));
    }

    [Fact]
    public void AnImageOnAFullWrappedRowStaysOnThatRow()
    {
        // 折り返す行は末尾まで文字で埋まり、カーソルは次の文字まで同じ行に留まる。絵はその行のもの。
        var buffer = new AnsiTerminalBuffer(Columns, 8);
        buffer.Process("\u001b]1337;File=inline=1:" + Png + "\u0007" + new string('x', Columns + 5));

        AnsiTerminalBuffer restored = Restore(Snapshot(buffer), 8);

        Assert.Single(ImagesOn(restored, 0));
        Assert.Empty(ImagesOn(restored, 1));
    }

    [Fact]
    public void AReplayImageOutsideTheReplayIsIgnored()
    {
        // 再生の外（cat されたファイルなど）で同じ並びが流れても、絵は置かない。
        var source = new AnsiTerminalBuffer(Columns, 8);
        source.Process("\u001bPq#0;2;100;0;0#0~~\u001b\\");
        string snapshot = Snapshot(source);
        int start = snapshot.IndexOf("\u001b]7770;img;", StringComparison.Ordinal);
        Assert.True(start >= 0);
        string imageOsc = snapshot[start..(snapshot.IndexOf('\u0007', start) + 1)];

        var buffer = new AnsiTerminalBuffer(Columns, 8);
        buffer.Process("x" + imageOsc);

        Assert.Empty(ImagesOn(buffer, 0));
    }
}
