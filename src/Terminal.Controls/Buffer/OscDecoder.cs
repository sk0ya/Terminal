using System.Globalization;
using System.Text;
using System.Windows.Media;

namespace Terminal.Buffer;

internal readonly record struct OscCommand(string Command, string Value);

internal readonly record struct OscTaskbarProgress(int State, int Progress);

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

        if (spec.StartsWith('#') && spec.Length >= 7 &&
            byte.TryParse(spec.AsSpan(1, 2), NumberStyles.HexNumber, null, out byte hashRed) &&
            byte.TryParse(spec.AsSpan(3, 2), NumberStyles.HexNumber, null, out byte hashGreen) &&
            byte.TryParse(spec.AsSpan(5, 2), NumberStyles.HexNumber, null, out byte hashBlue))
        {
            color = Color.FromRgb(hashRed, hashGreen, hashBlue);
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
        int length = Math.Min(2, hex.Length);
        if (length == 0)
        {
            value = 0;
            return false;
        }

        return byte.TryParse(hex.AsSpan(0, length), NumberStyles.HexNumber, null, out value);
    }
}
