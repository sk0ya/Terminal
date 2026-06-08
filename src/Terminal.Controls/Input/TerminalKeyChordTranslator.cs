using System.Windows.Input;

namespace Terminal.Input;

internal static class TerminalKeyChordTranslator
{
    public static string? TranslateCtrlChord(Key key)
    {
        return TranslateCtrlChord(key, ModifierKeys.Control);
    }

    public static string? TranslateCtrlChord(Key key, ModifierKeys modifiers)
    {
        if ((modifiers & ModifierKeys.Control) == 0)
        {
            return null;
        }

        string? chord = TranslateCtrlChordCore(key);
        if (chord is null)
        {
            return null;
        }

        return (modifiers & ModifierKeys.Alt) != 0
            ? $"\u001b{chord}"
            : chord;
    }

    public static string? TranslateCtrlChord(Key key, ModifierKeys modifiers, int modifyOtherKeysLevel)
    {
        if ((modifiers & ModifierKeys.Control) == 0)
            return null;

        if (modifyOtherKeysLevel >= 2 && key >= Key.A && key <= Key.Z)
        {
            bool shifted = (modifiers & ModifierKeys.Shift) != 0;
            int keyCode = (shifted ? 'A' : 'a') + (key - Key.A);
            return TerminalInputEncoder.EncodeModifyOtherKey(keyCode, modifiers);
        }

        return TranslateCtrlChord(key, modifiers);
    }

    private static string? TranslateCtrlChordCore(Key key)
    {
        if (key == Key.C)
        {
            return "\u0003";
        }

        if (key == Key.Space || key == Key.D2)
        {
            return "\0";
        }

        if (key == Key.Oem4 || key == Key.D3)
        {
            return "\u001b";
        }

        if (key == Key.Oem5 || key == Key.D4)
        {
            return "\u001c";
        }

        if (key == Key.Oem6 || key == Key.D5)
        {
            return "\u001d";
        }

        if (key == Key.D6)
        {
            return "\u001e";
        }

        if (key is Key.OemMinus or Key.Oem2 or Key.D7)
        {
            return "\u001f";
        }

        if (key == Key.D8)
        {
            return "\u007f";
        }

        if (key >= Key.A && key <= Key.Z)
        {
            char control = (char)(key - Key.A + 1);
            return control.ToString();
        }

        return null;
    }

    public static string? TranslateSpecialKey(
        Key key,
        ModifierKeys modifiers,
        bool applicationCursorKeys)
    {
        return key switch
        {
            Key.Enter => TerminalInputEncoder.EncodePrefixedControl("\r", modifiers),
            Key.Back => TerminalInputEncoder.EncodePrefixedControl("\u007f", modifiers),
            Key.Tab => TerminalInputEncoder.EncodeTabKey(modifiers),
            Key.Space => TerminalInputEncoder.EncodePrefixedControl(" ", modifiers),
            Key.Escape => TerminalInputEncoder.EncodePrefixedControl("\u001b", modifiers),
            Key.Up => TerminalInputEncoder.EncodeCursorKey('A', modifiers, applicationCursorKeys),
            Key.Down => TerminalInputEncoder.EncodeCursorKey('B', modifiers, applicationCursorKeys),
            Key.Right => TerminalInputEncoder.EncodeCursorKey('C', modifiers, applicationCursorKeys),
            Key.Left => TerminalInputEncoder.EncodeCursorKey('D', modifiers, applicationCursorKeys),
            Key.Home => TerminalInputEncoder.EncodeHomeEndKey('H', modifiers, applicationCursorKeys),
            Key.End => TerminalInputEncoder.EncodeHomeEndKey('F', modifiers, applicationCursorKeys),
            Key.Insert => TerminalInputEncoder.EncodeTildeKey(2, modifiers),
            Key.Delete => TerminalInputEncoder.EncodeTildeKey(3, modifiers),
            Key.PageUp => TerminalInputEncoder.EncodeTildeKey(5, modifiers),
            Key.PageDown => TerminalInputEncoder.EncodeTildeKey(6, modifiers),
            Key.F1 => TerminalInputEncoder.EncodeSs3FunctionKey('P', modifiers),
            Key.F2 => TerminalInputEncoder.EncodeSs3FunctionKey('Q', modifiers),
            Key.F3 => TerminalInputEncoder.EncodeSs3FunctionKey('R', modifiers),
            Key.F4 => TerminalInputEncoder.EncodeSs3FunctionKey('S', modifiers),
            Key.F5 => TerminalInputEncoder.EncodeTildeKey(15, modifiers),
            Key.F6 => TerminalInputEncoder.EncodeTildeKey(17, modifiers),
            Key.F7 => TerminalInputEncoder.EncodeTildeKey(18, modifiers),
            Key.F8 => TerminalInputEncoder.EncodeTildeKey(19, modifiers),
            Key.F9 => TerminalInputEncoder.EncodeTildeKey(20, modifiers),
            Key.F10 => TerminalInputEncoder.EncodeTildeKey(21, modifiers),
            Key.F11 => TerminalInputEncoder.EncodeTildeKey(23, modifiers),
            Key.F12 => TerminalInputEncoder.EncodeTildeKey(24, modifiers),
            _ => null
        };
    }

    public static string? TranslateSpecialKey(
        Key key,
        ModifierKeys modifiers,
        bool applicationCursorKeys,
        int modifyOtherKeysLevel,
        int kittyKeyboardFlags = 0)
    {
        if (TerminalInputEncoder.ShouldUseKittyEncoding(key, modifiers, kittyKeyboardFlags))
        {
            int? codePoint = GetKittyFunctionalKeyCode(key);
            if (codePoint.HasValue)
            {
                return TerminalInputEncoder.EncodeKittyKey(codePoint.Value, modifiers, kittyKeyboardFlags);
            }
        }

        if (modifyOtherKeysLevel >= 2 && modifiers != ModifierKeys.None)
        {
            switch (key)
            {
                case Key.Enter:
                    return TerminalInputEncoder.EncodeModifyOtherKey(13, modifiers);
                case Key.Tab:
                    return TerminalInputEncoder.EncodeModifyOtherKey(9, modifiers);
                case Key.Escape:
                    return TerminalInputEncoder.EncodeModifyOtherKey(27, modifiers);
                case Key.Back:
                    return TerminalInputEncoder.EncodeModifyOtherKey(127, modifiers);
                case Key.Space:
                    return TerminalInputEncoder.EncodeModifyOtherKey(32, modifiers);
            }
        }

        return TranslateSpecialKey(key, modifiers, applicationCursorKeys);
    }

    public static string? TranslateEnterKey(
        ModifierKeys modifiers,
        bool applicationCursorKeys,
        bool supportsTerminalInput)
    {
        return TranslateEnterKey(modifiers, applicationCursorKeys, supportsTerminalInput, 0, 0);
    }

    public static string? TranslateEnterKey(
        ModifierKeys modifiers,
        bool applicationCursorKeys,
        bool supportsTerminalInput,
        int modifyOtherKeysLevel,
        int kittyKeyboardFlags = 0)
    {
        if (!supportsTerminalInput && modifiers == ModifierKeys.None)
        {
            return "\r\n";
        }

        return TranslateSpecialKey(Key.Enter, modifiers, applicationCursorKeys, modifyOtherKeysLevel, kittyKeyboardFlags);
    }

    private static int? GetKittyFunctionalKeyCode(Key key)
    {
        return key switch
        {
            Key.Escape => 27,
            Key.Enter => 13,
            Key.Tab => 9,
            Key.Back => 127,
            Key.Space => 32,
            Key.Up => 57352,
            Key.Down => 57353,
            Key.Right => 57354,
            Key.Left => 57355,
            Key.Insert => 57348,
            Key.Delete => 57351,
            Key.PageUp => 57349,
            Key.PageDown => 57350,
            Key.Home => 57360,
            Key.End => 57361,
            Key.F1 => 57364,
            Key.F2 => 57365,
            Key.F3 => 57366,
            Key.F4 => 57367,
            Key.F5 => 57368,
            Key.F6 => 57369,
            Key.F7 => 57370,
            Key.F8 => 57371,
            Key.F9 => 57372,
            Key.F10 => 57373,
            Key.F11 => 57374,
            Key.F12 => 57375,
            _ => null
        };
    }
}
