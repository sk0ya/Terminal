using System.Windows.Input;

namespace ConPtyTerminal;

internal static class TerminalKeyChordTranslator
{
    public static string? TranslateCtrlChord(Key key)
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
            Key.Back => TerminalInputEncoder.EncodePrefixedControl("\b", modifiers),
            Key.Tab => TerminalInputEncoder.EncodeTabKey(modifiers),
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

    public static string? TranslateEnterKey(
        ModifierKeys modifiers,
        bool applicationCursorKeys,
        bool supportsTerminalInput)
    {
        if (!supportsTerminalInput && modifiers == ModifierKeys.None)
        {
            return "\r\n";
        }

        return TranslateSpecialKey(Key.Enter, modifiers, applicationCursorKeys);
    }
}
