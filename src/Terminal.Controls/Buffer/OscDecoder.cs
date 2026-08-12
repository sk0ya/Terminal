using System.Globalization;
using System.Text;
using System.Windows.Media;

namespace Terminal.Buffer;

internal readonly record struct OscCommand(string Command, string Value);

internal readonly record struct OscTaskbarProgress(int State, int Progress);

internal enum OscClipboardKind
{
    Invalid,
    Query,
    Set
}

internal readonly record struct OscClipboardPayload(
    OscClipboardKind Kind,
    string SelectionTargets,
    string? Text = null);

internal enum OscPaletteChangeKind
{
    Query,
    Set
}

internal readonly record struct OscPaletteChange(
    int Index,
    OscPaletteChangeKind Kind,
    Color Color = default);

internal readonly record struct OscPaletteReset(bool ResetAll, int[] Indices);

internal enum OscShellPayloadKind
{
    Unknown,
    CommandLine,
    Property,
    Zone
}

internal readonly record struct OscShellPayload(
    OscShellPayloadKind Kind,
    string? Value = null,
    string? PropertyName = null,
    ShellCommandZoneType? ZoneType = null,
    int? ExitCode = null);

internal static class OscDecoder
{
    public static OscCommand Decode(string payload)
    {
        int separatorIndex = payload.IndexOf(';');
        return separatorIndex >= 0
            ? new OscCommand(payload[..separatorIndex], payload[(separatorIndex + 1)..])
            : new OscCommand(payload, string.Empty);
    }

    public static bool TryDecodeTaskbarProgress(string value, out OscTaskbarProgress progress)
    {
        progress = default;
        string[] parts = value.Split(';');
        if (parts.Length < 2 || !int.TryParse(parts[1], out int state) || state is < 0 or > 4)
        {
            return false;
        }

        int percentage = 0;
        if (parts.Length >= 3 && int.TryParse(parts[2], out int rawProgress))
        {
            percentage = Math.Clamp(rawProgress, 0, 100);
        }

        progress = new OscTaskbarProgress(state, percentage);
        return true;
    }

    public static OscClipboardPayload DecodeClipboard(string value)
    {
        int separatorIndex = value.IndexOf(';');
        if (separatorIndex < 0)
        {
            return default;
        }

        string selectionTargets = value[..separatorIndex];
        string payload = value[(separatorIndex + 1)..];
        string normalizedTargets = string.IsNullOrEmpty(selectionTargets) ? "c" : selectionTargets;
        if (payload == "?")
        {
            return new OscClipboardPayload(OscClipboardKind.Query, normalizedTargets);
        }

        if (payload.Length == 0)
        {
            return new OscClipboardPayload(OscClipboardKind.Set, normalizedTargets, string.Empty);
        }

        try
        {
            byte[] decoded = Convert.FromBase64String(NormalizeBase64(payload));
            return new OscClipboardPayload(
                OscClipboardKind.Set,
                normalizedTargets,
                Encoding.UTF8.GetString(decoded));
        }
        catch (FormatException)
        {
            return default;
        }
    }

    public static OscPaletteChange[] DecodePaletteChanges(string value, int paletteLength)
    {
        string[] parts = value.Split(';');
        var changes = new List<OscPaletteChange>();
        for (int index = 0; index + 1 < parts.Length; index += 2)
        {
            if (!int.TryParse(parts[index], out int paletteIndex) ||
                paletteIndex < 0 ||
                paletteIndex >= paletteLength)
            {
                continue;
            }

            string colorSpec = parts[index + 1];
            if (colorSpec == "?")
            {
                changes.Add(new OscPaletteChange(paletteIndex, OscPaletteChangeKind.Query));
            }
            else if (TryParseColor(colorSpec, out Color color))
            {
                changes.Add(new OscPaletteChange(paletteIndex, OscPaletteChangeKind.Set, color));
            }
        }

        return changes.ToArray();
    }

    public static OscPaletteReset DecodePaletteReset(string value, int paletteLength)
    {
        if (value.Length == 0)
        {
            return new OscPaletteReset(ResetAll: true, []);
        }

        var indices = new List<int>();
        foreach (string part in value.Split(';'))
        {
            if (int.TryParse(part, out int paletteIndex) &&
                paletteIndex >= 0 &&
                paletteIndex < paletteLength)
            {
                indices.Add(paletteIndex);
            }
        }

        return new OscPaletteReset(ResetAll: false, indices.ToArray());
    }

    public static bool TryDecodeCurrentDirectory(string value, out string path)
    {
        path = value;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? directoryUri) && directoryUri.IsFile)
        {
            string decoded = Uri.UnescapeDataString(directoryUri.AbsolutePath);
            path = decoded.Length >= 3 && decoded[0] == '/' && char.IsLetter(decoded[1]) && decoded[2] == ':'
                ? decoded[1..]
                : decoded;
        }

        return true;
    }

    public static OscShellPayload DecodeShellIntegration(string value)
    {
        int separatorIndex = value.IndexOf(';');
        string type = separatorIndex >= 0 ? value[..separatorIndex] : value;
        string parameters = separatorIndex >= 0 ? value[(separatorIndex + 1)..] : string.Empty;

        if (type == "E")
        {
            return new OscShellPayload(
                OscShellPayloadKind.CommandLine,
                Value: DecodeShellEscapes(parameters));
        }

        if (type == "P")
        {
            int equals = parameters.IndexOf('=');
            if (equals > 0)
            {
                return new OscShellPayload(
                    OscShellPayloadKind.Property,
                    Value: DecodeShellEscapes(parameters[(equals + 1)..]),
                    PropertyName: parameters[..equals]);
            }

            return default;
        }

        ShellCommandZoneType? zoneType = type switch
        {
            "A" => ShellCommandZoneType.PromptStart,
            "B" => ShellCommandZoneType.CommandStart,
            "C" => ShellCommandZoneType.CommandExecuted,
            "D" => ShellCommandZoneType.CommandDone,
            _ => null
        };
        if (zoneType is null)
        {
            return default;
        }

        int? exitCode = null;
        if (zoneType == ShellCommandZoneType.CommandDone)
        {
            int separator = parameters.IndexOf(';');
            string exitCodeText = separator >= 0 ? parameters[..separator] : parameters;
            if (int.TryParse(exitCodeText, out int parsedExitCode))
            {
                exitCode = parsedExitCode;
            }
        }

        return new OscShellPayload(
            OscShellPayloadKind.Zone,
            ZoneType: zoneType,
            ExitCode: exitCode);
    }

    public static bool TryParseColor(string spec, out Color color)
    {
        color = default;
        if (spec.StartsWith("rgb:", StringComparison.OrdinalIgnoreCase))
        {
            string[] components = spec[4..].Split('/');
            if (components.Length != 3 ||
                !TryParseHexColorComponent(components[0], out byte red) ||
                !TryParseHexColorComponent(components[1], out byte green) ||
                !TryParseHexColorComponent(components[2], out byte blue))
            {
                return false;
            }

            color = Color.FromRgb(red, green, blue);
            return true;
        }

        if (spec.StartsWith('#') && TryParseHashColor(spec, out Color hashColor))
        {
            color = hashColor;
            return true;
        }

        return false;
    }

    public static string FormatColor(Color color)
    {
        return $"rgb:{color.R:x2}{color.R:x2}/{color.G:x2}{color.G:x2}/{color.B:x2}{color.B:x2}";
    }

    public static string DecodeShellEscapes(string encoded)
    {
        if (encoded.IndexOf('\\') < 0)
        {
            return encoded;
        }

        var builder = new StringBuilder(encoded.Length);
        for (int index = 0; index < encoded.Length; index++)
        {
            char current = encoded[index];
            if (current == '\\' && index + 1 < encoded.Length)
            {
                char next = encoded[index + 1];
                if (next == '\\')
                {
                    builder.Append('\\');
                    index++;
                    continue;
                }

                if (next == 'x' && index + 3 < encoded.Length &&
                    byte.TryParse(
                        encoded.AsSpan(index + 2, 2),
                        NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture,
                        out byte code))
                {
                    builder.Append((char)code);
                    index += 3;
                    continue;
                }
            }

            builder.Append(current);
        }

        return builder.ToString();
    }

    private static bool TryParseHexColorComponent(string hex, out byte value)
    {
        if (hex.Length is < 1 or > 4 ||
            !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint raw))
        {
            value = 0;
            return false;
        }

        uint max = (1u << (hex.Length * 4)) - 1;
        value = (byte)Math.Round(raw * 255d / max);
        return true;
    }

    private static bool TryParseHashColor(string spec, out Color color)
    {
        color = default;
        int digits = spec.Length - 1;
        if (digits is not (3 or 6 or 9 or 12))
        {
            return false;
        }

        int componentDigits = digits / 3;
        if (!TryParseHexColorComponent(spec.Substring(1, componentDigits), out byte red) ||
            !TryParseHexColorComponent(spec.Substring(1 + componentDigits, componentDigits), out byte green) ||
            !TryParseHexColorComponent(spec.Substring(1 + componentDigits * 2, componentDigits), out byte blue))
        {
            return false;
        }

        color = Color.FromRgb(red, green, blue);
        return true;
    }

    private static string NormalizeBase64(string payload)
    {
        int remainder = payload.Length % 4;
        return remainder == 0
            ? payload
            : payload.PadRight(payload.Length + (4 - remainder), '=');
    }
}
