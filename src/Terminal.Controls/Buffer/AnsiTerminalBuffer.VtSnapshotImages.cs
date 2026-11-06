using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Terminal.Buffer;

/// <summary>
/// 画面に置かれていた画像（Sixel・iTerm2・Kitty）を VT スナップショットに載せる。
/// <para>
/// 載せないと、常駐セッションへ付け直したビューでは画像が消え、画像が占めていた空の行だけが残る（Sixel はカーソルを
/// 画像の下へ進めるので、空いた行は正しく残る——消えるのは絵だけ）。元のシーケンスへ戻すのではなく、受け取ったあとの
/// <see cref="TerminalImage"/> をそのまま運ぶ：Sixel を送り直すと読む側がもう一度カーソルを進めて行がずれ、iTerm2 に
/// 直すと Sixel の仮想セルの大きさ（<see cref="TerminalImage.ScaledWidthCells"/>）が失われる。
/// </para>
/// <para>
/// 形式は再生の括弧と同じ私的な OSC <c>7770;img;&lt;属性&gt;;&lt;base64&gt;</c>で、再生の中だけで効く。画像を置くだけで
/// カーソルは動かさない（画像はもともと行に付いた重ね絵）。知らない版の読み手はこの OSC を黙って捨てるので、
/// 画像が無いだけで画面は今までどおり戻る。Sixel の生の画素（BGRA）は Deflate で縮める（パレット画なのでよく縮む）。
/// </para>
/// </summary>
internal sealed partial class AnsiTerminalBuffer
{
    internal const string ReplayImagePrefix = "img;";

    private static void AppendReplayImage(StringBuilder output, TerminalImage image)
    {
        if (image.Data.Length == 0)
        {
            return;
        }

        bool deflate = image.DataKind == TerminalImageDataKind.Bgra32;
        string payload = Convert.ToBase64String(deflate ? Deflate(image.Data) : image.Data);
        // 読む側の OSC の上限を超えるものは載せない（画像が1枚欠けるだけで、画面は戻る）。
        if (payload.Length > VtParser.MaxImageControlStringLength - 1024)
        {
            return;
        }

        var fields = new List<string>
        {
            "p=" + image.Protocol,
            "k=" + (deflate ? "b" : "e"),
            "pw=" + Int(image.PixelWidth),
            "ph=" + Int(image.PixelHeight),
            "c=" + Int(image.Column),
            "a=" + (image.PreserveAspectRatio ? "1" : "0"),
        };
        if (!string.IsNullOrEmpty(image.MimeType)) fields.Add("m=" + image.MimeType);
        if (image.WidthCells is int widthCells) fields.Add("wc=" + Int(widthCells));
        if (image.HeightCells is int heightCells) fields.Add("hc=" + Int(heightCells));
        if (image.WidthPixels is int widthPixels) fields.Add("wp=" + Int(widthPixels));
        if (image.HeightPixels is int heightPixels) fields.Add("hp=" + Int(heightPixels));
        if (image.ScaledWidthCells is double scaledWidth) fields.Add("sw=" + scaledWidth.ToString("R", CultureInfo.InvariantCulture));
        if (image.ScaledHeightCells is double scaledHeight) fields.Add("sh=" + scaledHeight.ToString("R", CultureInfo.InvariantCulture));

        output.Append("\u001b]").Append(ReplayOscCommand).Append(';').Append(ReplayImagePrefix)
            .Append(string.Join(',', fields)).Append(';').Append(payload).Append('\u0007');

        static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);
    }

    private bool TryDecodeReplayImage(ReadOnlySpan<char> value, [NotNullWhen(true)] out TerminalImage? image)
    {
        image = null;
        int separator = value.IndexOf(';');
        if (separator <= 0)
        {
            return false;
        }

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string item in value[..separator].ToString().Split(','))
        {
            int equals = item.IndexOf('=');
            if (equals > 0)
            {
                fields[item[..equals]] = item[(equals + 1)..];
            }
        }

        byte[] data;
        try
        {
            data = Convert.FromBase64String(value[(separator + 1)..].ToString());
        }
        catch (FormatException)
        {
            return false;
        }

        bool raw = fields.GetValueOrDefault("k") == "b";
        if (raw && !TryInflate(data, out data))
        {
            return false;
        }

        int pixelWidth = Int("pw") ?? 0;
        int pixelHeight = Int("ph") ?? 0;
        if (data.Length == 0 || data.Length > TerminalImageData.MaxDecodedBytes ||
            (raw && (pixelWidth <= 0 || pixelHeight <= 0 || (long)pixelWidth * pixelHeight * 4 != data.Length)))
        {
            return false;
        }

        image = new TerminalImage(
            data,
            raw ? TerminalImageDataKind.Bgra32 : TerminalImageDataKind.Encoded,
            fields.GetValueOrDefault("m") ?? (raw ? "image/bgra32" : null),
            pixelWidth,
            pixelHeight,
            Math.Clamp(Int("c") ?? 0, 0, Math.Max(0, _columns - 1)),
            Int("wc"),
            Int("hc"),
            Int("wp"),
            Int("hp"),
            fields.GetValueOrDefault("a") != "0")
        {
            ScaledWidthCells = Double("sw"),
            ScaledHeightCells = Double("sh"),
            Protocol = fields.GetValueOrDefault("p") ?? "",
        };
        return true;

        int? Int(string key) =>
            fields.TryGetValue(key, out string? text) &&
            int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed >= 0
                ? parsed : null;

        double? Double(string key) =>
            fields.TryGetValue(key, out string? text) &&
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) &&
            double.IsFinite(parsed) && parsed > 0
                ? parsed : null;
    }

    private static byte[] Deflate(byte[] data)
    {
        using var compressed = new MemoryStream();
        using (var deflate = new DeflateStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            deflate.Write(data);
        }

        return compressed.ToArray();
    }

    private static bool TryInflate(byte[] data, out byte[] inflated)
    {
        inflated = [];
        try
        {
            using var deflate = new DeflateStream(new MemoryStream(data), CompressionMode.Decompress);
            using var output = new MemoryStream();
            byte[] chunk = new byte[81920];
            int read;
            while ((read = deflate.Read(chunk, 0, chunk.Length)) > 0)
            {
                // 上限を超える伸長は捨てる（壊れた・悪意のある列で膨らませない）。
                if (output.Length + read > TerminalImageData.MaxDecodedBytes)
                {
                    return false;
                }

                output.Write(chunk, 0, read);
            }

            inflated = output.ToArray();
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }
}
