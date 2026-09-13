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
        => UnicodeWidth.GetGraphemeWidth(element, ambiguousAsWide);
}
