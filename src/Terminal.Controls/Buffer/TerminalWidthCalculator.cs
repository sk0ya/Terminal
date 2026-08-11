using System.Text;

using Terminal.Unicode;

namespace Terminal.Buffer;

/// <summary>
/// Shared terminal-cell width calculations used by the buffer and renderer.
/// </summary>
internal static class TerminalWidthCalculator
{
    public static int GetWidth(Rune rune, bool ambiguousAsWide = false) =>
        UnicodeWidth.GetWidth(rune, ambiguousAsWide);

    public static int EstimateGraphemeWidth(ReadOnlySpan<char> element, bool ambiguousAsWide)
    {
        bool hasVisibleRune = false;
        int maxWidth = 1;
        foreach (Rune rune in element.EnumerateRunes())
        {
            int width = GetWidth(rune, ambiguousAsWide);
            if (width <= 0)
            {
                continue;
            }

            hasVisibleRune = true;
            maxWidth = Math.Max(maxWidth, width);
        }

        return hasVisibleRune ? maxWidth : 1;
    }
}
