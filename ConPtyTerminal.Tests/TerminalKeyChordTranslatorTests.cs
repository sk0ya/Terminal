using System.Windows.Input;

namespace ConPtyTerminal.Tests;

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
    public void TranslateSpecialKeyEncodesNavigationWithModifiers()
    {
        string? sequence = TerminalKeyChordTranslator.TranslateSpecialKey(
            Key.PageDown,
            ModifierKeys.Control | ModifierKeys.Alt,
            applicationCursorKeys: false);

        Assert.Equal("\u001b[6;7~", sequence);
    }
}
