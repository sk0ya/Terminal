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

        if (UnicodeWidthData.IsWide(value) || UnicodeWidthData.IsEmojiPresentation(value))
        {
            return 2;
        }

        return ambiguousAsWide && UnicodeWidthData.IsAmbiguous(value) ? 2 : 1;
    }

    /// <summary>
    /// Returns the terminal cell width of one extended grapheme cluster.
    /// </summary>
    /// <remarks>
    /// A scalar width is not sufficient for emoji sequences. Keycaps are formed by an ASCII base and
    /// U+20E3, while U+FE0F/U+FE0E select emoji/text presentation for the preceding scalar. The
    /// buffer and renderer must use this same cluster-level rule or they will disagree about cursor
    /// movement and the position of every following cell.
    /// </remarks>
    internal static int GetGraphemeWidth(ReadOnlySpan<char> text, bool ambiguousAsWide = false)
    {
        bool hasVisibleRune = false;
        bool hasEmojiPresentationSelector = false;
        bool hasEmojiPresentationBase = false;
        bool hasTextPresentationSelector = false;
        bool hasKeycapBase = false;
        bool hasKeycapCombiningMark = false;
        int maxWidth = 1;
        int maxEastAsianWidth = 1;

        foreach (Rune rune in text.EnumerateRunes())
        {
            int value = rune.Value;
            if (value == 0xFE0F)
            {
                hasEmojiPresentationSelector = true;
                continue;
            }

            if (value == 0xFE0E)
            {
                hasTextPresentationSelector = true;
                continue;
            }

            if (value == 0x20E3)
            {
                hasKeycapCombiningMark = true;
                continue;
            }

            if (value is >= '0' and <= '9' or '#' or '*')
            {
                hasKeycapBase = true;
            }

            hasEmojiPresentationBase |=
                UnicodeWidthData.IsEmojiPresentation(value) ||
                UnicodeWidthData.IsEmojiVariationBase(value);

            int width = GetWidth(rune, ambiguousAsWide);
            if (width <= 0)
            {
                continue;
            }

            hasVisibleRune = true;
            maxEastAsianWidth = Math.Max(
                maxEastAsianWidth,
                GetEastAsianWidthWithoutEmojiPresentation(rune, ambiguousAsWide));
            maxWidth = Math.Max(maxWidth, width);
        }

        if (!hasVisibleRune)
        {
            return 1;
        }

        if (hasKeycapBase && hasKeycapCombiningMark)
        {
            return 2;
        }

        // VS15 takes precedence when malformed input contains both selectors; the text presentation
        // must remain narrow. Otherwise VS16 turns a narrow/ambiguous symbol into its emoji
        // presentation.
        if (hasTextPresentationSelector)
        {
            return maxEastAsianWidth;
        }

        if (hasEmojiPresentationSelector && hasEmojiPresentationBase)
        {
            return Math.Max(2, maxWidth);
        }

        return maxWidth;
    }

    private static int GetEastAsianWidthWithoutEmojiPresentation(Rune rune, bool ambiguousAsWide)
    {
        if (rune.IsAscii)
        {
            return 1;
        }

        int value = rune.Value;
        if (UnicodeWidthData.IsWide(value))
        {
            return 2;
        }

        return ambiguousAsWide && UnicodeWidthData.IsAmbiguous(value) ? 2 : 1;
    }

    private static bool IsZeroWidthModifier(int value) => value is
        0xFE0E or
        0xFE0F or
        >= 0x1F3FB and <= 0x1F3FF or
        >= 0xE0100 and <= 0xE01EF;
}
