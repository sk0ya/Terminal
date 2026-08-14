namespace Terminal.Buffer;

internal enum TerminalImageDataKind
{
    Encoded,
    Bgra32
}

/// <summary>
/// An immutable image anchored to a terminal cell. The byte array is either an encoded
/// image (normally PNG/JPEG) or premultiplied-free BGRA32 pixels produced by the Sixel decoder.
/// </summary>
/// <remarks>
/// An image never moves the cursor. ConPTY runs in its normal (non-passthrough) mode, so its own
/// screen model never sees these sequences and never advances its cursor by the rendered height.
/// Advancing ours would desynchronize the two models, and any program that repaints with absolute
/// positioning would then land its text rows above where we put it. The image is therefore a pure
/// overlay pinned to the cell it arrived at, painted behind the text of the rows it covers.
/// </remarks>
internal sealed record TerminalImage(
    byte[] Data,
    TerminalImageDataKind DataKind,
    string? MimeType,
    int PixelWidth,
    int PixelHeight,
    int Column,
    int? WidthCells,
    int? HeightCells,
    int? WidthPixels,
    int? HeightPixels,
    bool PreserveAspectRatio);

internal static class TerminalImageData
{
    public const int MaxDecodedBytes = 32 * 1024 * 1024;

    public static bool TryDecodeBase64(string value, out byte[] data)
    {
        data = [];
        try
        {
            string normalized = value.Replace("-", "+", StringComparison.Ordinal)
                .Replace("_", "/", StringComparison.Ordinal);
            int padding = normalized.Length % 4;
            if (padding != 0)
            {
                normalized = normalized.PadRight(normalized.Length + (4 - padding), '=');
            }

            data = Convert.FromBase64String(normalized);
            return data.Length > 0 && data.Length <= MaxDecodedBytes;
        }
        catch (FormatException)
        {
            data = [];
            return false;
        }
    }

    public static bool TryGetEncodedPixelSize(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = height = 0;
        // PNG IHDR.
        if (data.Length >= 24 && data[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) &&
            data[12..16].SequenceEqual("IHDR"u8))
        {
            width = ReadBigEndianInt32(data[16..20]);
            height = ReadBigEndianInt32(data[20..24]);
            return width > 0 && height > 0;
        }

        // JPEG: walk the marker stream until a SOF marker.
        if (data.Length >= 4 && data[0] == 0xff && data[1] == 0xd8)
        {
            int index = 2;
            while (index + 9 < data.Length)
            {
                if (data[index] != 0xff)
                {
                    index++;
                    continue;
                }

                while (index < data.Length && data[index] == 0xff) index++;
                if (index >= data.Length) break;
                byte marker = data[index++];
                if (marker is 0xd8 or 0xd9) continue;
                if (index + 2 > data.Length) break;
                int length = (data[index] << 8) | data[index + 1];
                if (length < 2 || index + length > data.Length) break;
                if (marker is >= 0xc0 and <= 0xcf && marker is not (0xc4 or 0xc8 or 0xcc))
                {
                    height = (data[index + 3] << 8) | data[index + 4];
                    width = (data[index + 5] << 8) | data[index + 6];
                    return width > 0 && height > 0;
                }

                index += length;
            }
        }

        return false;
    }

    private static int ReadBigEndianInt32(ReadOnlySpan<byte> value) =>
        (value[0] << 24) | (value[1] << 16) | (value[2] << 8) | value[3];
}
