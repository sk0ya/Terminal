using System.Globalization;
using System.Text;

namespace Terminal.Unicode;

internal static class UnicodeWidth
{
    internal static int GetWidth(Rune rune, bool ambiguousAsWide = false)
    {
        UnicodeCategory category = Rune.GetUnicodeCategory(rune);
        if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format)
        {
            return 0;
        }

        int value = rune.Value;
        if (IsZeroWidthModifier(value))
        {
            return 0;
        }

        if (rune.IsAscii)
        {
            return 1;
        }

        if (IsWide(value))
        {
            return 2;
        }

        if (ambiguousAsWide && IsAmbiguous(value))
        {
            return 2;
        }

        return 1;
    }

    private static bool IsZeroWidthModifier(int v) => v is
        // Note: U+200D (ZWJ) is UnicodeCategory.Format and is already caught above;
        // it is not listed here to avoid a misleading overlap.
        0xFE0E or                          // VS15 (text presentation)
        0xFE0F or                          // VS16 (emoji presentation)
        >= 0x1F3FB and <= 0x1F3FF or       // Emoji skin-tone modifiers
        >= 0xE0100 and <= 0xE01EF;         // Variation Selectors Supplement

    private static bool IsWide(int v) => v is
        >= 0x1100 and <= 0x115F or         // Hangul Jamo
        >= 0x2329 and <= 0x232A or         // Angle brackets (deprecated wide)
        >= 0x2E80 and <= 0xA4CF or         // CJK Radicals Supplement .. Yi Radicals
        >= 0xAC00 and <= 0xD7A3 or         // Hangul Syllables
        >= 0xF900 and <= 0xFAFF or         // CJK Compatibility Ideographs
        >= 0xFE10 and <= 0xFE19 or         // Vertical Forms
        >= 0xFE30 and <= 0xFE6F or         // CJK Compatibility Forms
        >= 0xFF00 and <= 0xFF60 or         // Fullwidth Forms
        >= 0xFFE0 and <= 0xFFE6 or         // Fullwidth Signs
        >= 0x1F1E6 and <= 0x1F1FF or       // Regional Indicators (flag emoji pairs)
        >= 0x1F300 and <= 0x1FAFF or       // Misc Symbols and Emoji
        >= 0x20000 and <= 0x3FFFD;         // CJK Extension B-F, Compatibility Ideographs

    // Unicode TR#11 East_Asian_Width=Ambiguous characters (major subsets).
    // When CjkAmbiguousWidthIsWide is enabled these are treated as width 2.
    internal static bool IsAmbiguous(int v) =>
        IsAmbiguousRange(v) || IsAmbiguousPoint(v);

    private static bool IsAmbiguousRange(int v) => v is
        >= 0x00A2 and <= 0x00A3 or         // Cent, Pound
        >= 0x00A5 and <= 0x00A6 or         // Yen, Broken bar
        >= 0x00AB and <= 0x00AC or         // Left angle quote, Not sign
        >= 0x00B0 and <= 0x00B4 or         // Degree .. Acute accent
        >= 0x00B6 and <= 0x00BA or         // Pilcrow .. Masculine ordinal
        >= 0x00BC and <= 0x00BF or         // Vulgar fractions, Inverted question
        >= 0x00D7 and <= 0x00D8 or         // Multiplication, O-stroke
        >= 0x00DE and <= 0x00E1 or         // Thorn .. A-acute
        >= 0x00E8 and <= 0x00EA or         // E-grave, E-acute, E-circumflex
        >= 0x00EC and <= 0x00ED or         // I-grave, I-acute
        >= 0x00F2 and <= 0x00F3 or         // O-grave, O-acute
        >= 0x00F7 and <= 0x00FA or         // Division, O-stroke, U-grave, U-acute
        >= 0x02C9 and <= 0x02CB or         // Tone modifiers
        >= 0x02D8 and <= 0x02DB or         // Breve .. Ogonek
        >= 0x0391 and <= 0x03A1 or         // Greek capital A-P
        >= 0x03A3 and <= 0x03A9 or         // Greek capital Sigma-Omega
        >= 0x03B1 and <= 0x03C1 or         // Greek small alpha-rho
        >= 0x03C3 and <= 0x03C9 or         // Greek small sigma-omega
        >= 0x0410 and <= 0x044F or         // Cyrillic A-ya (А-я)
        >= 0x2013 and <= 0x2016 or         // En dash .. Double vertical line
        >= 0x2018 and <= 0x2019 or         // Left/Right single quotation marks
        >= 0x201C and <= 0x201D or         // Left/Right double quotation marks
        >= 0x2020 and <= 0x2022 or         // Dagger, Double dagger, Bullet
        >= 0x2024 and <= 0x2027 or         // One dot leader .. Hyphenation point
        >= 0x2032 and <= 0x2033 or         // Prime, Double prime
        >= 0x2081 and <= 0x2084 or         // Subscript digits 1-4
        >= 0x2153 and <= 0x2154 or         // Vulgar fractions 1/3, 2/3
        >= 0x215B and <= 0x215E or         // Vulgar fractions 1/8-7/8
        >= 0x2160 and <= 0x216B or         // Roman numerals I-XII
        >= 0x2170 and <= 0x2179 or         // Small roman numerals i-x
        >= 0x2190 and <= 0x2199 or         // Basic arrows ←↑→↓ etc.
        >= 0x2202 and <= 0x2203 or         // Partial differential, There exists
        >= 0x2207 and <= 0x2208 or         // Nabla, Element of
        >= 0x2227 and <= 0x222C or         // Logical AND/OR, intersect/union, integrals
        >= 0x2234 and <= 0x2237 or         // Therefore, Because, Ratio, Proportion
        >= 0x223C and <= 0x223D or         // Tilde operator, Reversed tilde
        >= 0x2260 and <= 0x2261 or         // Not equal, Identical to
        >= 0x2264 and <= 0x2267 or         // Less/greater-or-equal (two forms each)
        >= 0x226A and <= 0x226B or         // Much less/greater than
        >= 0x226E and <= 0x226F or         // Not less/greater than
        >= 0x2282 and <= 0x2283 or         // Subset, Superset of
        >= 0x2286 and <= 0x2287 or         // Subset/Superset of or equal
        >= 0x2460 and <= 0x24E9 or         // Enclosed alphanumerics ①-ⓩ
        >= 0x24EB and <= 0x254B or         // Enclosed alphanumeric supplement
        >= 0x2550 and <= 0x2573 or         // Box Drawing (double/light mixed)
        >= 0x2580 and <= 0x258F or         // Block Elements
        >= 0x2592 and <= 0x2595 or         // Shade patterns, partial blocks
        >= 0x25A0 and <= 0x25A1 or         // Black/White square ■□
        >= 0x25A3 and <= 0x25A9 or         // Patterned squares
        >= 0x25B2 and <= 0x25B3 or         // Black/White up-pointing triangle
        >= 0x25B6 and <= 0x25B7 or         // Black/White right-pointing triangle
        >= 0x25BC and <= 0x25BD or         // Black/White down-pointing triangle
        >= 0x25C0 and <= 0x25C1 or         // Black/White left-pointing triangle
        >= 0x25C6 and <= 0x25C8 or         // Black/White diamond, diamond w/dot
        >= 0x25CE and <= 0x25D1 or         // Bullseye, Black circle, Half circles
        >= 0x25E2 and <= 0x25E5 or         // Corner triangles
        >= 0x2605 and <= 0x2606 or         // Black/White star ★☆
        >= 0x260E and <= 0x260F or         // Telephone symbols ☎☏
        >= 0x2660 and <= 0x2661 or         // Spade/Heart outline ♠♡
        >= 0x2663 and <= 0x2665 or         // Club/Diamond/Heart suit ♣♤♥
        >= 0x2667 and <= 0x266A or         // White suits, music notes ♧♨♩♪
        >= 0x266C and <= 0x266D or         // Beamed notes, Flat ♬♭
        >= 0x2776 and <= 0x277F or         // Dingbat negative circled digits ❶-❿
        >= 0x2B55 and <= 0x2B59;           // Heavy circle, dotted circles ⭕-⭙

    private static bool IsAmbiguousPoint(int v) => v is
        0x00A1 or  // ¡
        0x00A4 or  // ¤
        0x00A7 or  // §
        0x00A8 or  // ¨
        0x00AA or  // ª
        // Note: U+00AD (soft hyphen) is UnicodeCategory.Format → caught before IsAmbiguous; omitted here.
        0x00AE or  // ®
        0x00AF or  // Macron ¯
        0x00C6 or  // Æ
        0x00D0 or  // Ð
        0x00E6 or  // æ
        0x00F0 or  // ð
        0x00FC or  // ü
        0x00FE or  // þ
        0x0101 or  // ā
        0x0111 or  // đ
        0x0113 or  // ē
        0x011B or  // ě
        0x0126 or  // Ħ
        0x0127 or  // ħ
        0x012B or  // ī
        0x0131 or  // ı
        0x0132 or  // Ĳ
        0x0133 or  // ĳ
        0x0138 or  // ĸ
        0x013F or  // Ŀ
        0x0140 or  // ŀ
        0x0141 or  // Ł
        0x0142 or  // ł
        0x0144 or  // ń
        0x0148 or  // ň
        0x0149 or  // ŉ
        0x014A or  // Ŋ
        0x014B or  // ŋ
        0x014D or  // ō
        0x0152 or  // Œ
        0x0153 or  // œ
        0x0166 or  // Ŧ
        0x0167 or  // ŧ
        0x016B or  // ū
        0x01CE or  // ǎ
        0x01D0 or  // ǐ
        0x01D2 or  // ǒ
        0x01D4 or  // ǔ
        0x01D6 or  // ǖ
        0x01D8 or  // ǘ
        0x01DA or  // ǚ
        0x01DC or  // ǜ
        0x0251 or  // ɑ (Latin alpha)
        0x0261 or  // ɡ (Script g)
        0x02C4 or  // ˄
        0x02C7 or  // ˇ
        0x02CD or  // ˍ
        0x02D0 or  // ː
        0x02DD or  // ˝
        0x02DF or  // ˟
        0x0401 or  // Ё
        0x0451 or  // ё
        0x2010 or  // ‐ Hyphen
        0x2030 or  // ‰ Per mille
        0x2035 or  // ‵ Reversed prime
        0x203B or  // ※ Reference mark
        0x203E or  // ‾ Overline
        0x2074 or  // ⁴ Superscript four
        0x207F or  // ⁿ Superscript n
        0x20AC or  // € Euro sign
        0x2103 or  // ℃
        0x2105 or  // ℅
        0x2109 or  // ℉
        0x2113 or  // ℓ
        0x2116 or  // №
        0x2121 or  // ℡
        0x2122 or  // ™
        0x2126 or  // Ω (Ohm)
        0x212B or  // Å (Angstrom)
        0x2189 or  // ↉
        0x21B8 or  // ↸
        0x21B9 or  // ↹
        0x21D2 or  // ⇒
        0x21D4 or  // ⇔
        0x21E7 or  // ⇧
        0x2200 or  // ∀
        0x220B or  // ∋
        0x220F or  // ∏
        0x2211 or  // ∑
        0x2215 or  // ∕
        0x221A or  // √
        0x221D or  // ∝
        0x221E or  // ∞
        0x221F or  // ∟
        0x2220 or  // ∠
        0x2223 or  // ∣
        0x2225 or  // ∥
        0x222E or  // ∮
        0x2248 or  // ≈
        0x224C or  // ≌
        0x2252 or  // ≒
        0x2295 or  // ⊕
        0x2299 or  // ⊙
        0x22A5 or  // ⊥
        0x22BF or  // ⊿
        0x2312 or  // ⌒
        0x25CB or  // ○
        0x25EF or  // ◯
        0x2609 or  // ☉
        0x261C or  // ☜
        0x261E or  // ☞
        0x266F or  // ♯
        0x269E or  // ⚞
        0x269F or  // ⚟
        0x26BE or  // ⚾
        0x26BF or  // ⚿
        0x26E3 or  // ⛣
        0x273D or  // ✽
        0x2757 or  // ❗
        0xFFFD;    // Replacement character
}
