using System.Windows.Media;

namespace Terminal.Rendering;

internal sealed class FontFallbackResolver
{
    // The fonts full-width text falls back to, in order. "Yu Gothic" comes before "Yu Gothic UI"
    // because the UI cut narrows its kana to save room in dialogs; on a grid that leaves kana
    // visibly thinner than the kanji next to them, with slack on both sides of every one.
    internal static readonly string[] WideFallbackFamilyNames =
    [
        "Yu Gothic",
        "Yu Gothic UI",
        "Meiryo",
        "MS Gothic",
        "SimSun",
        "NanumGothicCoding",
    ];

    // The ranges the width table counts as two cells, in FontFamilyMap.Unicode syntax.
    internal const string WideUnicodeRanges =
        "1100-115F,2E80-303E,3041-33FF,3400-4DBF,4E00-9FFF,A000-A4CF,AC00-D7A3,F900-FAFF," +
        "FE30-FE4F,FF00-FF60,FFE0-FFE6,20000-2FFFD,30000-3FFFD";

    // HIRAGANA LETTER A: a font that has it draws full-width text on its own.
    internal const int WideProbeCodepoint = 0x3042;

    private static readonly string[] FallbackFamilyNames = ["Segoe UI Emoji", .. WideFallbackFamilyNames];

    private readonly GlyphTypeface _primaryGlyphTypeface;
    private readonly GlyphTypeface[] _fallbackGlyphTypefaces;
    private readonly Dictionary<int, GlyphTypeface?> _cache = [];

    public FontFallbackResolver(Typeface primaryTypeface)
    {
        primaryTypeface.TryGetGlyphTypeface(out GlyphTypeface? primary);
        _primaryGlyphTypeface = primary!;

        var fallbacks = new List<GlyphTypeface>();
        foreach (string name in FallbackFamilyNames)
        {
            var tf = new Typeface(name);
            if (tf.TryGetGlyphTypeface(out GlyphTypeface? gtf))
            {
                fallbacks.Add(gtf);
            }
        }

        _fallbackGlyphTypefaces = [.. fallbacks];
    }

    public void ClearCache() => _cache.Clear();

    public GlyphTypeface? Resolve(int codepoint)
    {
        if (_cache.TryGetValue(codepoint, out GlyphTypeface? cached))
        {
            return cached;
        }

        GlyphTypeface? result = null;
        if (_primaryGlyphTypeface is not null &&
            _primaryGlyphTypeface.CharacterToGlyphMap.ContainsKey(codepoint))
        {
            result = _primaryGlyphTypeface;
        }
        else
        {
            foreach (GlyphTypeface fallback in _fallbackGlyphTypefaces)
            {
                if (fallback.CharacterToGlyphMap.ContainsKey(codepoint))
                {
                    result = fallback;
                    break;
                }
            }
        }

        _cache[codepoint] = result;
        return result;
    }
}
