using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using Terminal.Buffer;

namespace Terminal.Tabs;

/// <summary>
/// <see cref="TerminalTabView.InlineImageReceived"/> の引数。端末に届いたインライン画像1枚（Sixel・iTerm2 の
/// <c>OSC 1337;File=</c>・Kitty Graphics）。端末の中の描画はこれまでどおりで、ホストは同じ絵を別の場所
/// （横の面など）で見せ直すのに使う。
/// </summary>
public sealed class TerminalInlineImageEventArgs : EventArgs
{
    private readonly TerminalImage _image;
    private byte[]? _encoded;

    internal TerminalInlineImageEventArgs(TerminalImage image, DateTime receivedUtc)
    {
        _image = image;
        ReceivedUtc = receivedUtc;
    }

    /// <summary>運んできたシーケンス：<c>sixel</c>・<c>iterm2</c>・<c>kitty</c>。</summary>
    public string Protocol => _image.Protocol;

    /// <summary>届いた時刻（UTC）。</summary>
    public DateTime ReceivedUtc { get; }

    /// <summary>画像の画素の幅（分からなければ 0）。</summary>
    public int PixelWidth => _image.PixelWidth;

    /// <summary>画像の画素の高さ（分からなければ 0）。</summary>
    public int PixelHeight => _image.PixelHeight;

    /// <summary><see cref="GetEncodedData"/> が返すものの種類（<c>image/png</c> か <c>image/jpeg</c>）。</summary>
    public string MimeType => _image.DataKind == TerminalImageDataKind.Encoded && _image.MimeType is { } mime ? mime : "image/png";

    /// <summary>
    /// 画像のファイルとしての中身（PNG／JPEG）。届いたものが符号化済みならそのまま、Sixel・Kitty の生の画素なら
    /// PNG に符号化する（初回の呼び出しで1回だけ。どのスレッドから呼んでもよい——大きな画像は裏で呼ぶ）。
    /// 符号化できなければ空。
    /// </summary>
    public byte[] GetEncodedData()
    {
        if (_encoded is { } done)
        {
            return done;
        }

        byte[] result;
        if (_image.DataKind == TerminalImageDataKind.Encoded)
        {
            result = _image.Data;
        }
        else
        {
            result = EncodePng(_image);
        }

        _encoded = result;
        return result;
    }

    private static byte[] EncodePng(TerminalImage image)
    {
        if (image.PixelWidth <= 0 || image.PixelHeight <= 0 ||
            image.Data.Length != image.PixelWidth * image.PixelHeight * 4)
        {
            return [];
        }

        try
        {
            BitmapSource bitmap = BitmapSource.Create(
                image.PixelWidth, image.PixelHeight, 96, 96, PixelFormats.Bgra32, null, image.Data, image.PixelWidth * 4);
            bitmap.Freeze();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or InvalidOperationException)
        {
            return [];
        }
    }
}
