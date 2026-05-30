using System.Windows.Input;

using Terminal.Input;

namespace Terminal.Tests;

public sealed class TerminalKeyChordTranslatorTests
{
    [Theory]
    [InlineData(Key.Space, "\0")]
    [InlineData(Key.D2, "\0")]
    [InlineData(Key.D3, "\u001b")]
    [InlineData(Key.Oem4, "\u001b")]
    [InlineData(Key.D4, "\u001c")]
    [InlineData(Key.Oem5, "\u001c")]
    [InlineData(Key.D5, "\u001d")]
    [InlineData(Key.Oem6, "\u001d")]
    [InlineData(Key.D6, "\u001e")]
    [InlineData(Key.D7, "\u001f")]
    [InlineData(Key.Oem2, "\u001f")]
    [InlineData(Key.OemMinus, "\u001f")]
    [InlineData(Key.D8, "\u007f")]
    public void TranslateCtrlChordMapsCommonAsciiControlSequences(Key key, string expected)
    {
        Assert.Equal(expected, TerminalKeyChordTranslator.TranslateCtrlChord(key));
    }

    [Fact]
    public void TranslateCtrlChordMapsAlphabetKeys()
    {
        Assert.Equal("\u0001", TerminalKeyChordTranslator.TranslateCtrlChord(Key.A));
        Assert.Equal("\u001a", TerminalKeyChordTranslator.TranslateCtrlChord(Key.Z));
    }

    [Fact]
    public void TranslateCtrlChordPrefixesEscapeForAltCtrlChord()
    {
        Assert.Equal(
            "\u001b\u0001",
            TerminalKeyChordTranslator.TranslateCtrlChord(Key.A, ModifierKeys.Control | ModifierKeys.Alt));
    }

    [Fact]
    public void TranslateCtrlChordAllowsShiftWithControlChord()
    {
        Assert.Equal(
            "\u001f",
            TerminalKeyChordTranslator.TranslateCtrlChord(Key.OemMinus, ModifierKeys.Control | ModifierKeys.Shift));
    }

    [Fact]
    public void TranslateCtrlChordReturnsNullWithoutControlModifier()
    {
        Assert.Null(TerminalKeyChordTranslator.TranslateCtrlChord(Key.A, ModifierKeys.Alt));
    }

    [Fact]
    public void TranslateSpecialKeyEncodesNavigationWithModifiers()
    {
        string? sequence = TerminalKeyChordTranslator.TranslateSpecialKey(
            Key.PageDown,
            ModifierKeys.Control | ModifierKeys.Alt,
            applicationCursorKeys: false);

        Assert.Equal("\u001b[6;7~", sequence);
    }

    [Fact]
    public void TranslateSpecialKeyEncodesEnter()
    {
        Assert.Equal("\r", TerminalKeyChordTranslator.TranslateSpecialKey(
            Key.Enter,
            ModifierKeys.None,
            applicationCursorKeys: false));
    }

    [Fact]
    public void TranslateEnterKeyUsesCrInTerminalInputMode()
    {
        Assert.Equal("\r", TerminalKeyChordTranslator.TranslateEnterKey(
            ModifierKeys.None,
            applicationCursorKeys: false,
            supportsTerminalInput: true));
    }

    [Fact]
    public void TranslateEnterKeyUsesCrLfWhenTerminalInputIsUnavailable()
    {
        Assert.Equal("\r\n", TerminalKeyChordTranslator.TranslateEnterKey(
            ModifierKeys.None,
            applicationCursorKeys: false,
            supportsTerminalInput: false));
    }

    [Fact]
    public void TranslateSpecialKeyWithKittyFlagsEncodesShiftEnterAsCsiU()
    {
        string? result = TerminalKeyChordTranslator.TranslateSpecialKey(
            Key.Enter,
            ModifierKeys.Shift,
            applicationCursorKeys: false,
            kittyKeyboardFlags: 1);

        Assert.Equal("[13;2u", result);
    }

    [Fact]
    public void TranslateSpecialKeyWithKittyFlagsEncodesShiftTabAsCsiU()
    {
        string? result = TerminalKeyChordTranslator.TranslateSpecialKey(
            Key.Tab,
            ModifierKeys.Shift,
            applicationCursorKeys: false,
            kittyKeyboardFlags: 1);

        Assert.Equal("[9;2u", result);
    }

    [Fact]
    public void TranslateSpecialKeyWithKittyFlagsEncodesCtrlEnterAsCsiU()
    {
        string? result = TerminalKeyChordTranslator.TranslateSpecialKey(
            Key.Enter,
            ModifierKeys.Control,
            applicationCursorKeys: false,
            kittyKeyboardFlags: 1);

        Assert.Equal("[13;5u", result);
    }

    [Fact]
    public void TranslateSpecialKeyWithKittyFlagsZeroFallsBackToLegacy()
    {
        string? result = TerminalKeyChordTranslator.TranslateSpecialKey(
            Key.Enter,
            ModifierKeys.Shift,
            applicationCursorKeys: false,
            kittyKeyboardFlags: 0);

        Assert.Equal("\r", result);
    }

    [Fact]
    public void TranslateEnterKeyWithKittyFlagsEncodesShiftEnterAsCsiU()
    {
        string? result = TerminalKeyChordTranslator.TranslateEnterKey(
            ModifierKeys.Shift,
            applicationCursorKeys: false,
            supportsTerminalInput: true,
            kittyKeyboardFlags: 1);

        Assert.Equal("[13;2u", result);
    }

    [Fact]
    public void TranslateSpecialKeyWithKittyFlagsEncodesUpArrowAsCsiU()
    {
        string? result = TerminalKeyChordTranslator.TranslateSpecialKey(
            Key.Up,
            ModifierKeys.None,
            applicationCursorKeys: false,
            kittyKeyboardFlags: 8);

        Assert.Equal("[57352u", result);
    }

    [Fact]
    public void TranslateSpecialKeyWithKittyFlagsEncodesAltSpaceAsCsiU()
    {
        string? result = TerminalKeyChordTranslator.TranslateSpecialKey(
            Key.Space,
            ModifierKeys.Alt,
            applicationCursorKeys: false,
            kittyKeyboardFlags: 1);

        Assert.Equal("[32;3u", result);
    }
}
