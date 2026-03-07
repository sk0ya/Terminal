using System.Text;
using System.Windows.Input;

namespace ConPtyTerminal.Tests;

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
}
