using System.Globalization;

namespace Terminal.Rendering;

/// <summary>
/// Decoded Sixel image as a top-down, tightly-packed BGRA pixel buffer. Unset pixels are fully
/// transparent so the image composites cleanly over the terminal background.
/// </summary>
internal readonly record struct SixelImageData(int Width, int Height, byte[] Bgra);

/// <summary>
/// Decodes the body of a Sixel <c>DCS … q … ST</c> sequence into pixels. The implementation is
/// pure (no WPF types) so it can be unit tested directly. It supports raster attributes (<c>"</c>),
/// colour definition/selection (<c>#</c>, RGB and HLS), run-length repeats (<c>!</c>), graphics
/// carriage return (<c>$</c>) and line feed (<c>-</c>), and the 6-pixel sixel data bytes
/// (<c>?</c>..<c>~</c>).
/// </summary>
internal static class SixelDecoder
{
    private const int MaxDimension = 10_000;
    private const int PaletteSize = 256;

    // VT340 default 16-colour palette (percent-scaled RGB converted to 0..255).
    private static readonly int[] DefaultPalette = BuildDefaultPalette();

    /// <summary>
    /// Decodes the sixel data that follows the <c>q</c> introducer. Returns <c>null</c> when the
    /// data contains no pixels.
    /// </summary>
    public static SixelImageData? Decode(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        ScanExtents(body, out int width, out int height);
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var bgra = new byte[width * height * 4];
        Render(body, width, height, bgra);
        return new SixelImageData(width, height, bgra);
    }

    private static void ScanExtents(string body, out int width, out int height)
    {
        int x = 0;
        int band = 0;
        int maxX = 0;
        int maxY = 0;
        int rasterWidth = 0;
        int rasterHeight = 0;

        for (int index = 0; index < body.Length;)
        {
            char ch = body[index];
            switch (ch)
            {
                case '"':
                    {
                        index++;
                        int[] values = ReadParameters(body, ref index, out _);
                        if (values.Length >= 4)
                        {
                            rasterWidth = values[2];
                            rasterHeight = values[3];
                        }

                        break;
                    }
                case '#':
                    index++;
                    ReadParameters(body, ref index, out _);
                    break;
                case '!':
                    {
                        index++;
                        int repeat = ReadInteger(body, ref index, defaultValue: 1);
                        char data = SkipToSixelData(body, ref index);
                        if (data != '\0')
                        {
                            int value = data - 0x3F;
                            x += Math.Max(repeat, 0);
                            maxX = Math.Max(maxX, x);
                            maxY = Math.Max(maxY, (band * 6) + HighestSetRow(value));
                        }

                        break;
                    }
                case '$':
                    x = 0;
                    index++;
                    break;
                case '-':
                    band++;
                    x = 0;
                    index++;
                    break;
                default:
                    if (ch >= '?' && ch <= '~')
                    {
                        int value = ch - 0x3F;
                        x++;
                        maxX = Math.Max(maxX, x);
                        maxY = Math.Max(maxY, (band * 6) + HighestSetRow(value));
                    }

                    index++;
                    break;
            }
        }

        width = Math.Clamp(Math.Max(maxX, rasterWidth), 0, MaxDimension);
        height = Math.Clamp(Math.Max(maxY, rasterHeight), 0, MaxDimension);
    }

    private static void Render(string body, int width, int height, byte[] bgra)
    {
        int[] palette = (int[])DefaultPalette.Clone();
        int color = palette[0];
        int x = 0;
        int band = 0;

        for (int index = 0; index < body.Length;)
        {
            char ch = body[index];
            switch (ch)
            {
                case '"':
                    index++;
                    ReadParameters(body, ref index, out _);
                    break;
                case '#':
                    {
                        index++;
                        int[] values = ReadParameters(body, ref index, out int count);
                        if (count == 0)
                        {
                            break;
                        }

                        int register = Math.Clamp(values[0], 0, PaletteSize - 1);
                        if (count >= 5)
                        {
                            palette[register] = ConvertColor(values[1], values[2], values[3], values[4]);
                        }

                        color = palette[register];
                        break;
                    }
                case '!':
                    {
                        index++;
                        int repeat = ReadInteger(body, ref index, defaultValue: 1);
                        char data = SkipToSixelData(body, ref index);
                        if (data != '\0')
                        {
                            PlotColumn(bgra, width, height, ref x, band, data - 0x3F, color, Math.Max(repeat, 0));
                        }

                        break;
                    }
                case '$':
                    x = 0;
                    index++;
                    break;
                case '-':
                    band++;
                    x = 0;
                    index++;
                    break;
                default:
                    if (ch >= '?' && ch <= '~')
                    {
                        PlotColumn(bgra, width, height, ref x, band, ch - 0x3F, color, 1);
                    }

                    index++;
                    break;
            }
        }
    }

    private static void PlotColumn(byte[] bgra, int width, int height, ref int x, int band, int value, int color, int repeat)
    {
        for (int run = 0; run < repeat; run++, x++)
        {
            if (x < 0 || x >= width || value == 0)
            {
                continue;
            }

            for (int bit = 0; bit < 6; bit++)
            {
                if ((value & (1 << bit)) == 0)
                {
                    continue;
                }

                int y = (band * 6) + bit;
                if (y >= height)
                {
                    continue;
                }

                int offset = ((y * width) + x) * 4;
                bgra[offset] = (byte)(color & 0xFF);          // B
                bgra[offset + 1] = (byte)((color >> 8) & 0xFF); // G
                bgra[offset + 2] = (byte)((color >> 16) & 0xFF); // R
                bgra[offset + 3] = 0xFF;                        // A
            }
        }
    }

    private static int HighestSetRow(int value)
    {
        // Returns the 1-based index of the lowest pixel touched within a 6-pixel band (0 when empty),
        // i.e. one past the highest set bit, so it contributes the band's pixel height.
        int highest = 0;
        for (int bit = 0; bit < 6; bit++)
        {
            if ((value & (1 << bit)) != 0)
            {
                highest = bit + 1;
            }
        }

        return highest;
    }

    private static char SkipToSixelData(string body, ref int index)
    {
        while (index < body.Length)
        {
            char ch = body[index];
            if (ch >= '?' && ch <= '~')
            {
                index++;
                return ch;
            }

            index++;
        }

        return '\0';
    }

    private static int[] ReadParameters(string body, ref int index, out int count)
    {
        var values = new List<int>(5);
        while (true)
        {
            values.Add(ReadInteger(body, ref index, defaultValue: 0));
            if (index < body.Length && body[index] == ';')
            {
                index++;
                continue;
            }

            break;
        }

        count = values.Count;
        return values.ToArray();
    }

    private static int ReadInteger(string body, ref int index, int defaultValue)
    {
        int start = index;
        while (index < body.Length && body[index] >= '0' && body[index] <= '9')
        {
            index++;
        }

        if (index == start)
        {
            return defaultValue;
        }

        return int.TryParse(body.AsSpan(start, index - start), NumberStyles.None, CultureInfo.InvariantCulture, out int value)
            ? value
            : defaultValue;
    }

    private static int ConvertColor(int model, int a, int b, int c)
    {
        if (model == 1)
        {
            // HLS: a = hue (0..360), b = lightness (0..100), c = saturation (0..100).
            return HlsToRgb(a, b, c);
        }

        // RGB (model 2 or unspecified): components are percentages (0..100).
        int r = ScalePercent(a);
        int g = ScalePercent(b);
        int bl = ScalePercent(c);
        return (r << 16) | (g << 8) | bl;
    }

    private static int ScalePercent(int percent)
    {
        int clamped = Math.Clamp(percent, 0, 100);
        return (clamped * 255 + 50) / 100;
    }

    private static int HlsToRgb(int hueDegrees, int lightnessPercent, int saturationPercent)
    {
        double h = ((hueDegrees % 360) + 360) % 360;
        double l = Math.Clamp(lightnessPercent, 0, 100) / 100.0;
        double s = Math.Clamp(saturationPercent, 0, 100) / 100.0;

        double c = (1 - Math.Abs((2 * l) - 1)) * s;
        // Sixel HLS places 0° at blue and rotates differently from HSL; the conventional mapping
        // used by libsixel rotates the hue so that the documented primaries line up.
        double hp = (((h + 240) % 360)) / 60.0;
        double xComponent = c * (1 - Math.Abs((hp % 2) - 1));
        double m = l - (c / 2);

        double r1 = 0;
        double g1 = 0;
        double b1 = 0;
        switch ((int)hp)
        {
            case 0: r1 = c; g1 = xComponent; break;
            case 1: r1 = xComponent; g1 = c; break;
            case 2: g1 = c; b1 = xComponent; break;
            case 3: g1 = xComponent; b1 = c; break;
            case 4: r1 = xComponent; b1 = c; break;
            default: r1 = c; b1 = xComponent; break;
        }

        int r = (int)Math.Round((r1 + m) * 255);
        int g = (int)Math.Round((g1 + m) * 255);
        int b = (int)Math.Round((b1 + m) * 255);
        return (Math.Clamp(r, 0, 255) << 16) | (Math.Clamp(g, 0, 255) << 8) | Math.Clamp(b, 0, 255);
    }

    private static int[] BuildDefaultPalette()
    {
        // VT340 default colour map (index → RGB percentages), the remainder left black.
        (int R, int G, int B)[] defaults =
        [
            (0, 0, 0), (20, 20, 80), (80, 13, 13), (20, 80, 20),
            (80, 20, 80), (20, 80, 80), (80, 80, 20), (53, 53, 53),
            (26, 26, 26), (33, 33, 60), (60, 26, 26), (33, 60, 33),
            (60, 33, 60), (33, 60, 60), (60, 60, 33), (80, 80, 80),
        ];

        var palette = new int[PaletteSize];
        for (int index = 0; index < defaults.Length; index++)
        {
            (int r, int g, int b) = defaults[index];
            palette[index] = (ScalePercent(r) << 16) | (ScalePercent(g) << 8) | ScalePercent(b);
        }

        return palette;
    }
}
