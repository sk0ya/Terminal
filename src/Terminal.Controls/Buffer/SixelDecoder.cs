namespace Terminal.Buffer;

/// <summary>Small, allocation-bounded Sixel raster decoder for terminal inline graphics.</summary>
internal static class SixelDecoder
{
    private const int MaxWidth = 16_384;
    private const int MaxHeight = 16_384;
    private const int MaxPixels = TerminalImageData.MaxDecodedBytes / 4;

    public static bool TryDecode(string payload, out byte[] pixels, out int width, out int height)
    {
        pixels = [];
        width = height = 0;
        var canvas = new Canvas();
        int index = 0;
        int currentColor = 0;
        int repeat = 1;

        while (index < payload.Length)
        {
            char ch = payload[index++];
            if (ch is >= '?' and <= '~')
            {
                int value = ch - '?';
                for (int count = 0; count < repeat; count++)
                {
                    for (int bit = 0; bit < 6; bit++)
                    {
                        if ((value & (1 << bit)) != 0)
                        {
                            canvas.SetPixel(canvas.X, canvas.Y + bit, currentColor);
                        }
                    }

                    canvas.X++;
                }

                repeat = 1;
                continue;
            }

            if (ch == '!')
            {
                int start = index;
                while (index < payload.Length && char.IsDigit(payload[index])) index++;
                repeat = int.TryParse(payload[start..index], out int parsed)
                    ? Math.Clamp(parsed, 1, MaxWidth)
                    : 1;
                continue;
            }

            if (ch == '#')
            {
                int start = index;
                while (index < payload.Length && char.IsDigit(payload[index])) index++;
                _ = int.TryParse(payload[start..index], out currentColor);
                currentColor = Math.Clamp(currentColor, 0, 255);
                if (index < payload.Length && payload[index] == ';')
                {
                    int parameterStart = ++index;
                    while (index < payload.Length && payload[index] is >= '0' and <= '9' or ';') index++;
                    string[] parameters = payload[parameterStart..index].Split(';');
                    if (parameters.Length >= 4 &&
                        int.TryParse(parameters[0], out int mode) &&
                        int.TryParse(parameters[1], out int p1) &&
                        int.TryParse(parameters[2], out int p2) &&
                        int.TryParse(parameters[3], out int p3))
                    {
                        // #Pc;2;Px;Py;Pz carries exactly three colour components after the mode.
                        if (mode == 2)
                        {
                            canvas.SetColor(currentColor, p1, p2, p3);
                        }
                        else if (mode == 1)
                        {
                            canvas.SetHlsColor(currentColor, p1, p2, p3);
                        }
                    }
                }

                continue;
            }

            if (ch == '"')
            {
                int start = index;
                while (index < payload.Length && payload[index] is >= '0' and <= '9' or ';') index++;
                string[] parameters = payload[start..index].Split(';');
                if (parameters.Length >= 4 && int.TryParse(parameters[2], out int rasterWidth) &&
                    int.TryParse(parameters[3], out int rasterHeight))
                {
                    canvas.SetRasterSize(rasterWidth, rasterHeight);
                }

                continue;
            }

            if (ch == '$')
            {
                canvas.X = 0;
            }
            else if (ch == '-')
            {
                canvas.X = 0;
                canvas.Y += 6;
            }
        }

        if (canvas.Width == 0 || canvas.Height == 0)
        {
            return false;
        }

        canvas.TrimToContent(out width, out height, out pixels);
        return width > 0 && height > 0 && pixels.Length <= TerminalImageData.MaxDecodedBytes;
    }

    private sealed class Canvas
    {
        private byte[] _pixels = [];
        private readonly uint[] _colors = new uint[256];
        private int _maxX = -1;
        private int _maxY = -1;

        public Canvas()
        {
            _colors[0] = 0xff000000;
        }

        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; private set; }
        public int Height { get; private set; }

        public void SetRasterSize(int width, int height)
        {
            Ensure(Math.Clamp(width, 1, MaxWidth), Math.Clamp(height, 1, MaxHeight));
        }

        public void SetColor(int index, int red, int green, int blue)
        {
            _colors[index] = 0xff000000u | ((uint)Math.Clamp(red, 0, 100) * 255 / 100 << 16) |
                ((uint)Math.Clamp(green, 0, 100) * 255 / 100 << 8) |
                ((uint)Math.Clamp(blue, 0, 100) * 255 / 100);
        }

        public void SetHlsColor(int index, int hue, int lightness, int saturation)
        {
            // HLS conversion used by Sixel. Keeping this branch makes common xterm palette
            // sequences work while RGB (the format most emitters use) stays allocation-free.
            double h = (hue % 360) / 360.0;
            double l = Math.Clamp(lightness, 0, 100) / 100.0;
            double s = Math.Clamp(saturation, 0, 100) / 100.0;
            double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            double p = 2 * l - q;
            double[] channels = [h + 1.0 / 3, h, h - 1.0 / 3];
            byte Convert(double t)
            {
                if (t < 0) t += 1;
                if (t > 1) t -= 1;
                double value = t < 1.0 / 6 ? p + (q - p) * 6 * t :
                    t < 1.0 / 2 ? q : t < 2.0 / 3 ? p + (q - p) * (2.0 / 3 - t) * 6 : p;
                return (byte)Math.Round(value * 255);
            }

            _colors[index] = 0xff000000u | ((uint)Convert(channels[0]) << 16) |
                ((uint)Convert(channels[1]) << 8) | Convert(channels[2]);
        }

        public void SetPixel(int x, int y, int color)
        {
            if ((uint)x >= MaxWidth || (uint)y >= MaxHeight) return;
            Ensure(Math.Max(Width, x + 1), Math.Max(Height, y + 1));
            if ((uint)x >= Width || (uint)y >= Height) return;
            uint value = _colors[Math.Clamp(color, 0, 255)];
            int offset = (y * Width + x) * 4;
            _pixels[offset] = (byte)value;
            _pixels[offset + 1] = (byte)(value >> 8);
            _pixels[offset + 2] = (byte)(value >> 16);
            _pixels[offset + 3] = 255;
            _maxX = Math.Max(_maxX, x);
            _maxY = Math.Max(_maxY, y);
        }

        public void TrimToContent(out int width, out int height, out byte[] pixels)
        {
            width = Math.Max(1, _maxX + 1);
            height = Math.Max(1, _maxY + 1);
            pixels = new byte[width * height * 4];
            for (int row = 0; row < height; row++)
            {
                Array.Copy(_pixels, row * Width * 4, pixels, row * width * 4, width * 4);
            }
        }

        private void Ensure(int width, int height)
        {
            // Growth is monotonic. Raster attributes are allowed to appear after pixel data, and
            // shrinking here would silently truncate everything already decoded.
            int targetWidth = Math.Clamp(Math.Max(width, Width), 1, MaxWidth);
            int targetHeight = Math.Clamp(Math.Max(height, Height), 1, MaxHeight);
            if (targetWidth <= Width && targetHeight <= Height) return;

            // Grow geometrically: a payload that omits raster attributes reaches its final size one
            // pixel column at a time, and an exact-fit reallocation per column is quadratic.
            int grownWidth = targetWidth > Width ? Math.Min(MaxWidth, Math.Max(targetWidth, Math.Max(Width * 2, 64))) : targetWidth;
            int grownHeight = targetHeight > Height ? Math.Min(MaxHeight, Math.Max(targetHeight, Math.Max(Height * 2, 64))) : targetHeight;
            if ((long)grownWidth * grownHeight <= MaxPixels)
            {
                (targetWidth, targetHeight) = (grownWidth, grownHeight);
            }
            else if ((long)targetWidth * targetHeight > MaxPixels)
            {
                targetHeight = Math.Max(1, (int)(MaxPixels / targetWidth));
            }

            // The pixel budget can leave no room to grow; keep the canvas as it is rather than
            // rebuilding it smaller.
            if (targetWidth < Width || targetHeight < Height) return;

            byte[] resized = new byte[targetWidth * targetHeight * 4];
            for (int row = 0; row < Height; row++)
            {
                Array.Copy(_pixels, row * Width * 4, resized, row * targetWidth * 4, Width * 4);
            }

            _pixels = resized;
            Width = targetWidth;
            Height = targetHeight;
        }
    }
}
