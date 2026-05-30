using System.Windows.Media;

namespace Terminal.Rendering;

internal sealed class FontFallbackResolver
{
    private static readonly string[] FallbackFamilyNames =
    [
        "Segoe UI Emoji",
        "Yu Gothic UI",
        "Meiryo",
        "MS Gothic",
        "SimSun",
        "NanumGothicCoding",
    ];

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
