using System.Text;

using Terminal.Unicode;

namespace Terminal.Tests;

public sealed class UnicodeWidthTests
{
    [Theory]
    [InlineData('A', 1)]
    [InlineData('Z', 1)]
    [InlineData(' ', 1)]
    [InlineData('0', 1)]
    public void AsciiChars_AreWidth1(char ch, int expected)
    {
        Assert.Equal(expected, UnicodeWidth.GetWidth(new Rune(ch)));
    }

    [Theory]
    [InlineData(0x3042, 2)]  // あ (Hiragana)
    [InlineData(0x4E2D, 2)]  // 中 (CJK)
    [InlineData(0xAC00, 2)]  // 가 (Hangul)
    [InlineData(0xFF21, 2)]  // Ａ (Fullwidth)
    [InlineData(0x1F600, 2)] // 😀 (Emoji)
    [InlineData(0x1F1EF, 2)] // 🇯 (Regional Indicator J)
    public void WideChars_AreWidth2(int codepoint, int expected)
    {
        Assert.Equal(expected, UnicodeWidth.GetWidth(new Rune(codepoint)));
    }

    [Theory]
    [InlineData(0x0301)]  // Combining Acute Accent
    [InlineData(0x200D)]  // ZWJ
    [InlineData(0xFE0F)]  // VS16 (Emoji presentation)
    [InlineData(0x1F3FB)] // Skin tone modifier
    public void ZeroWidthChars_AreWidth0(int codepoint)
    {
        Assert.Equal(0, UnicodeWidth.GetWidth(new Rune(codepoint)));
    }

    [Theory]
    [InlineData(0x00AE, 1)]  // ® (Registered Sign) — Narrow by default
    [InlineData(0x0391, 1)]  // Α (Greek Alpha) — Narrow by default
    [InlineData(0x20AC, 1)]  // € (Euro) — Narrow by default
    [InlineData(0x2192, 1)]  // → (Arrow) — Narrow by default
    [InlineData(0x2460, 1)]  // ① (Enclosed 1) — Narrow by default
    [InlineData(0x25A0, 1)]  // ■ (Black square) — Narrow by default
    public void AmbiguousChars_DefaultToWidth1(int codepoint, int expected)
    {
        Assert.Equal(expected, UnicodeWidth.GetWidth(new Rune(codepoint), ambiguousAsWide: false));
    }

    [Theory]
    [InlineData(0x0391, 2)]  // Α (Greek Alpha) → Wide when enabled
    [InlineData(0x0410, 2)]  // А (Cyrillic А) → Wide when enabled
    [InlineData(0x20AC, 2)]  // € (Euro) → Wide when enabled
    [InlineData(0x2192, 2)]  // → (Arrow) → Wide when enabled
    [InlineData(0x2460, 2)]  // ① (Enclosed 1) → Wide when enabled
    [InlineData(0x25A0, 2)]  // ■ (Black square) → Wide when enabled
    public void AmbiguousChars_BecomeWidth2_WhenEnabled(int codepoint, int expected)
    {
        Assert.Equal(expected, UnicodeWidth.GetWidth(new Rune(codepoint), ambiguousAsWide: true));
    }

    [Fact]
    public void WideChars_StayWidth2_Regardless_Of_AmbiguousFlag()
    {
        Rune kanji = new Rune(0x4E2D); // 中
        Assert.Equal(2, UnicodeWidth.GetWidth(kanji, ambiguousAsWide: false));
        Assert.Equal(2, UnicodeWidth.GetWidth(kanji, ambiguousAsWide: true));
    }

    [Fact]
    public void AmbiguousWidthIsWide_PropagatesThrough_Buffer()
    {
        var buffer = new Terminal.Buffer.AnsiTerminalBuffer(40, 5);
        buffer.AmbiguousWidthIsWide = true;

        buffer.Process("Α");

        var snapshot = buffer.CreateRenderSnapshot(showCursor: false);
        Assert.True(snapshot.AmbiguousWidthIsWide);
        Assert.Equal(2, buffer.CursorColumn);
    }

    [Fact]
    public void AmbiguousWidthIsWide_False_TreatsGreekAsNarrow()
    {
        var buffer = new Terminal.Buffer.AnsiTerminalBuffer(40, 5);
        buffer.AmbiguousWidthIsWide = false;

        buffer.Process("Α");

        Assert.Equal(1, buffer.CursorColumn);
    }
}
