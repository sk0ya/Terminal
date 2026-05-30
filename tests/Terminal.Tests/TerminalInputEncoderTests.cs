using System.Text;
using System.Windows.Input;

using Terminal.Buffer;
using Terminal.Input;

namespace Terminal.Tests;

public sealed class TerminalInputEncoderTests
{
    [Fact]
    public void CursorKeyUsesApplicationModeWithoutModifiers()
    {
        Assert.Equal("\u001bOA", TerminalInputEncoder.EncodeCursorKey('A', ModifierKeys.None, applicationCursorKeys: true));
        Assert.Equal("\u001b[A", TerminalInputEncoder.EncodeCursorKey('A', ModifierKeys.None, applicationCursorKeys: false));
    }

    [Fact]
    public void ModifiedKeysUseCsiModifierParameters()
    {
        ModifierKeys modifiers = ModifierKeys.Shift | ModifierKeys.Alt;

        Assert.Equal("\u001b[1;4D", TerminalInputEncoder.EncodeCursorKey('D', modifiers, applicationCursorKeys: false));
        Assert.Equal("\u001b[1;4Z", TerminalInputEncoder.EncodeTabKey(modifiers));
        Assert.Equal("\u001b[23;5~", TerminalInputEncoder.EncodeTildeKey(23, ModifierKeys.Control));
    }

    [Fact]
    public void MouseModifierBitsMatchXtermEncoding()
    {
        ModifierKeys modifiers = ModifierKeys.Alt | ModifierKeys.Control;

        Assert.Equal(24, TerminalInputEncoder.GetMouseModifierBits(modifiers));
    }

    [Fact]
    public void EncodesLegacyMouseSequenceAsRawBytes()
    {
        byte[] encoded = TerminalInputEncoder.EncodeMouseSequence(TerminalMouseEncoding.Default, 0, 10, 20, sgrRelease: false);

        Assert.Equal(new byte[] { 0x1B, (byte)'[', (byte)'M', 32, 42, 52 }, encoded);
    }

    [Fact]
    public void EncodesSgrAndUrxvtMouseSequencesAsTextProtocols()
    {
        byte[] sgr = TerminalInputEncoder.EncodeMouseSequence(TerminalMouseEncoding.Sgr, 35, 10, 20, sgrRelease: true);
        byte[] urxvt = TerminalInputEncoder.EncodeMouseSequence(TerminalMouseEncoding.Urxvt, 3, 10, 20, sgrRelease: false);

        Assert.Equal("\u001b[<35;10;20m", Encoding.ASCII.GetString(sgr));
        Assert.Equal("\u001b[35;10;20M", Encoding.ASCII.GetString(urxvt));
    }

    [Fact]
    public void EncodesUtf8MouseCoordinatesBeyondLegacyLimit()
    {
        byte[] encoded = TerminalInputEncoder.EncodeMouseSequence(TerminalMouseEncoding.Utf8, 0, 500, 400, sgrRelease: false);
        string text = Encoding.UTF8.GetString(encoded);
        string expected = "\u001b[M" +
            char.ConvertFromUtf32(32) +
            char.ConvertFromUtf32(532) +
            char.ConvertFromUtf32(432);

        Assert.Equal(expected, text);
    }

    [Fact]
    public void KittyModifierParamHasBaseOfOne()
    {
        Assert.Equal(1, TerminalInputEncoder.GetKittyModifierParameter(ModifierKeys.None));
        Assert.Equal(2, TerminalInputEncoder.GetKittyModifierParameter(ModifierKeys.Shift));
        Assert.Equal(3, TerminalInputEncoder.GetKittyModifierParameter(ModifierKeys.Alt));
        Assert.Equal(5, TerminalInputEncoder.GetKittyModifierParameter(ModifierKeys.Control));
        Assert.Equal(8, TerminalInputEncoder.GetKittyModifierParameter(ModifierKeys.Shift | ModifierKeys.Alt | ModifierKeys.Control));
    }

    [Fact]
    public void EncodeKittyKeyNoModifierOmitsModifier()
    {
        string result = TerminalInputEncoder.EncodeKittyKey(13, ModifierKeys.None, kittyFlags: 1);

        Assert.Equal("[13u", result);
    }

    [Fact]
    public void EncodeKittyKeyWithShiftIncludesModifier()
    {
        string result = TerminalInputEncoder.EncodeKittyKey(13, ModifierKeys.Shift, kittyFlags: 1);

        Assert.Equal("[13;2u", result);
    }

    [Fact]
    public void EncodeKittyKeyWithEventTypeWhenBit1Set()
    {
        string result = TerminalInputEncoder.EncodeKittyKey(13, ModifierKeys.None, kittyFlags: 3, eventType: KittyEventType.Release);

        Assert.Equal("[13;1:3u", result);
    }

    [Fact]
    public void EncodeKittyKeyPressEventOmitsEventTypeWhenBit1NotSet()
    {
        string result = TerminalInputEncoder.EncodeKittyKey(13, ModifierKeys.Shift, kittyFlags: 1, eventType: KittyEventType.Press);

        Assert.Equal("[13;2u", result);
    }

    [Fact]
    public void ShouldUseKittyEncodingReturnsFalseWhenFlagsZero()
    {
        Assert.False(TerminalInputEncoder.ShouldUseKittyEncoding(Key.Enter, ModifierKeys.Shift, kittyFlags: 0));
    }

    [Fact]
    public void ShouldUseKittyEncodingReturnsTrueForShiftEnterWhenEnabled()
    {
        Assert.True(TerminalInputEncoder.ShouldUseKittyEncoding(Key.Enter, ModifierKeys.Shift, kittyFlags: 1));
    }

    [Fact]
    public void ShouldUseKittyEncodingReturnsFalseForPlainEnterWithDisambiguateOnly()
    {
        Assert.False(TerminalInputEncoder.ShouldUseKittyEncoding(Key.Enter, ModifierKeys.None, kittyFlags: 1));
    }

    [Fact]
    public void ShouldUseKittyEncodingReturnsTrueForAllKeysWhenBit3Set()
    {
        Assert.True(TerminalInputEncoder.ShouldUseKittyEncoding(Key.A, ModifierKeys.None, kittyFlags: 8));
        Assert.True(TerminalInputEncoder.ShouldUseKittyEncoding(Key.Enter, ModifierKeys.None, kittyFlags: 8));
    }
}
