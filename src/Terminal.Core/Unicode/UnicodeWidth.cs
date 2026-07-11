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

    private static bool IsZeroWidthModifier(int value) => value is
        0xFE0E or
        0xFE0F or
        >= 0x1F3FB and <= 0x1F3FF or
        >= 0xE0100 and <= 0xE01EF;
}
