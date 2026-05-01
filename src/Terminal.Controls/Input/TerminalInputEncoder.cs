using System.Text;
using System.Windows.Input;

using Terminal.Buffer;

namespace Terminal.Input;

internal static class TerminalInputEncoder
{
    public static string? EncodePrefixedControl(string text, ModifierKeys modifiers)
    {
        return modifiers switch
        {
            ModifierKeys.None or ModifierKeys.Shift => text,
            ModifierKeys.Alt or (ModifierKeys.Alt | ModifierKeys.Shift) => $"\u001b{text}",
            _ => null
        };
    }

    public static string? EncodeTabKey(ModifierKeys modifiers)
    {
        return modifiers switch
        {
            ModifierKeys.None => "\t",
            ModifierKeys.Shift => "\u001b[Z",
            ModifierKeys.Alt => "\u001b\t",
            ModifierKeys.Alt | ModifierKeys.Shift => $"\u001b[1;{GetCsiModifierParameter(modifiers)}Z",
            _ => null
        };
    }

    public static string EncodeCursorKey(char final, ModifierKeys modifiers, bool applicationCursorKeys)
    {
        if (modifiers == ModifierKeys.None)
        {
            return applicationCursorKeys ? $"\u001bO{final}" : $"\u001b[{final}";
        }

        return $"\u001b[1;{GetCsiModifierParameter(modifiers)}{final}";
    }

    public static string EncodeHomeEndKey(char final, ModifierKeys modifiers, bool applicationCursorKeys)
    {
        if (modifiers == ModifierKeys.None)
        {
            return applicationCursorKeys ? $"\u001bO{final}" : $"\u001b[{final}";
        }

        return $"\u001b[1;{GetCsiModifierParameter(modifiers)}{final}";
    }

    public static string EncodeSs3FunctionKey(char final, ModifierKeys modifiers)
    {
        return modifiers == ModifierKeys.None
            ? $"\u001bO{final}"
            : $"\u001b[1;{GetCsiModifierParameter(modifiers)}{final}";
    }

    public static string EncodeTildeKey(int code, ModifierKeys modifiers)
    {
        return modifiers == ModifierKeys.None
            ? $"\u001b[{code}~"
            : $"\u001b[{code};{GetCsiModifierParameter(modifiers)}~";
    }

    public static int GetCsiModifierParameter(ModifierKeys modifiers)
    {
        int parameter = 1;
        if ((modifiers & ModifierKeys.Shift) != 0)
        {
            parameter += 1;
        }

        if ((modifiers & ModifierKeys.Alt) != 0)
        {
            parameter += 2;
        }

        if ((modifiers & ModifierKeys.Control) != 0)
        {
            parameter += 4;
        }

        return parameter;
    }

    public static int GetMouseModifierBits(ModifierKeys modifiers)
    {
        int bits = 0;
        if ((modifiers & ModifierKeys.Shift) != 0)
        {
            bits += 4;
        }

        if ((modifiers & ModifierKeys.Alt) != 0)
        {
            bits += 8;
        }

        if ((modifiers & ModifierKeys.Control) != 0)
        {
            bits += 16;
        }

        return bits;
    }

    public static byte[] EncodeMouseSequence(TerminalMouseEncoding encoding, int code, int column, int row, bool sgrRelease)
    {
        return encoding switch
        {
            TerminalMouseEncoding.Sgr => Encoding.ASCII.GetBytes($"\u001b[<{code};{column};{row}{(sgrRelease ? 'm' : 'M')}"),
            TerminalMouseEncoding.Urxvt => Encoding.ASCII.GetBytes($"\u001b[{code + 32};{column};{row}M"),
            TerminalMouseEncoding.Utf8 => EncodeUtf8MouseSequenceBytes(code, column, row),
            _ => EncodeLegacyMouseSequenceBytes(code, column, row)
        };
    }

    private static byte[] EncodeLegacyMouseSequenceBytes(int code, int column, int row)
    {
        return
        [
            0x1B,
            (byte)'[',
            (byte)'M',
            (byte)Math.Clamp(code + 32, 32, 255),
            (byte)Math.Clamp(column + 32, 32, 255),
            (byte)Math.Clamp(row + 32, 32, 255)
        ];
    }

    private static byte[] EncodeUtf8MouseSequenceBytes(int code, int column, int row)
    {
        var bytes = new List<byte>(16)
        {
            0x1B,
            (byte)'[',
            (byte)'M'
        };

        AppendUtf8MouseValue(bytes, code + 32);
        AppendUtf8MouseValue(bytes, column + 32);
        AppendUtf8MouseValue(bytes, row + 32);
        return [.. bytes];
    }

    private static void AppendUtf8MouseValue(List<byte> bytes, int value)
    {
        int normalized = Math.Clamp(value, 32, 0x10FFFF);
        Span<byte> buffer = stackalloc byte[4];
        Rune rune = new(normalized);
        int written = rune.EncodeToUtf8(buffer);
        for (int index = 0; index < written; index++)
        {
            bytes.Add(buffer[index]);
        }
    }
}
