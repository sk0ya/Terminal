using System.Windows.Media;

namespace Terminal.Buffer;

internal readonly record struct SgrParam(int Code, int[]? Sub = null);

internal static class SgrInterpreter
{
    public static SgrParam[] Parse(string parameterText)
    {
        if (string.IsNullOrEmpty(parameterText))
        {
            return [];
        }

        string[] tokens = parameterText.Split(';');
        var result = new SgrParam[tokens.Length];
        for (int index = 0; index < tokens.Length; index++)
        {
            string token = tokens[index];
            int colon = token.IndexOf(':');
            if (colon < 0)
            {
                result[index] = new SgrParam(int.TryParse(token, out int simpleCode) ? simpleCode : 0);
                continue;
            }

            int code = int.TryParse(token.AsSpan(0, colon), out int parsedCode) ? parsedCode : 0;
            string[] subParts = token[(colon + 1)..].Split(':');
            var nonEmpty = new List<int>(subParts.Length);
            foreach (string part in subParts)
            {
                if (part.Length > 0)
                {
                    nonEmpty.Add(int.TryParse(part, out int subParameter) ? subParameter : 0);
                }
            }

            result[index] = nonEmpty.Count > 0
                ? new SgrParam(code, nonEmpty.ToArray())
                : new SgrParam(code);
        }

        return result;
    }

    public static UnderlineStyle ResolveUnderlineStyle(SgrParam token)
    {
        if (token.Sub is null || token.Sub.Length == 0)
        {
            return UnderlineStyle.Single;
        }

        return token.Sub[0] switch
        {
            0 => UnderlineStyle.None,
            2 => UnderlineStyle.Double,
            3 => UnderlineStyle.Curly,
            4 => UnderlineStyle.Dotted,
            5 => UnderlineStyle.Dashed,
            _ => UnderlineStyle.Single
        };
    }

    public static bool TryReadExtendedColor(
        SgrParam[] tokens,
        ref int index,
        IReadOnlyList<Color> ansiPalette,
        Color fallback,
        out Color color)
    {
        color = default;
        SgrParam current = tokens[index];

        if (current.Sub is { Length: >= 1 })
        {
            int mode = current.Sub[0];
            if (mode == 5 && current.Sub.Length >= 2)
            {
                color = ResolveXtermColor(current.Sub[1], ansiPalette, fallback);
                return true;
            }

            if (mode == 2 && current.Sub.Length >= 4)
            {
                color = Color.FromRgb(
                    ClampByte(current.Sub[1]),
                    ClampByte(current.Sub[2]),
                    ClampByte(current.Sub[3]));
                return true;
            }

            return false;
        }

        if (index + 1 >= tokens.Length)
        {
            return false;
        }

        int legacyMode = tokens[index + 1].Code;
        if (legacyMode == 5 && index + 2 < tokens.Length)
        {
            color = ResolveXtermColor(tokens[index + 2].Code, ansiPalette, fallback);
            index += 2;
            return true;
        }

        if (legacyMode == 2 && index + 4 < tokens.Length)
        {
            color = Color.FromRgb(
                ClampByte(tokens[index + 2].Code),
                ClampByte(tokens[index + 3].Code),
                ClampByte(tokens[index + 4].Code));
            index += 4;
            return true;
        }

        return false;
    }

    public static Color ResolveXtermColor(int index, IReadOnlyList<Color> ansiPalette, Color fallback)
    {
        if (index < 0)
        {
            return fallback;
        }

        if (index < ansiPalette.Count)
        {
            return ansiPalette[index];
        }

        if (index <= 231)
        {
            int value = index - 16;
            int red = value / 36;
            int green = (value / 6) % 6;
            int blue = value % 6;
            return Color.FromRgb(
                ScaleCubeComponent(red),
                ScaleCubeComponent(green),
                ScaleCubeComponent(blue));
        }

        if (index <= 255)
        {
            byte shade = (byte)(8 + ((index - 232) * 10));
            return Color.FromRgb(shade, shade, shade);
        }

        return fallback;
    }

    private static byte ClampByte(int value) => (byte)Math.Clamp(value, 0, 255);

    private static byte ScaleCubeComponent(int value) =>
        value == 0 ? (byte)0 : (byte)(55 + (value * 40));
}
