using System.Windows.Media;

namespace Terminal;

internal static class TerminalFontCatalog
{
    public const string DefaultFontFamilyName = "Cascadia Mono";

    private static readonly string[] PreferredFontFamilyNames =
    [
        DefaultFontFamilyName,
        "Cascadia Code",
        "Consolas",
        "MS Gothic",
        "Lucida Console",
        "JetBrains Mono",
        "Fira Code",
        "Hack",
        "Courier New"
    ];

    private static readonly Lazy<IReadOnlyList<string>> AvailableFontFamilyNames = new(() =>
        BuildAvailableFontFamilyNames(Fonts.SystemFontFamilies.Select(fontFamily => fontFamily.Source)));

    public static IReadOnlyList<string> CreateFontFamilyNames()
    {
        return AvailableFontFamilyNames.Value;
    }

    public static string NormalizeFontFamilyName(string? fontFamilyName)
    {
        return NormalizeFontFamilyName(fontFamilyName, CreateFontFamilyNames());
    }

    public static FontFamily CreateFontFamily(string? fontFamilyName)
    {
        return new FontFamily(NormalizeFontFamilyName(fontFamilyName));
    }

    internal static string NormalizeFontFamilyName(string? fontFamilyName, IReadOnlyList<string> availableFontFamilies)
    {
        if (!string.IsNullOrWhiteSpace(fontFamilyName))
        {
            string requested = fontFamilyName.Trim();
            foreach (string available in availableFontFamilies)
            {
                if (string.Equals(available, requested, StringComparison.OrdinalIgnoreCase))
                {
                    return available;
                }
            }
        }

        return availableFontFamilies.Count > 0
            ? availableFontFamilies[0]
            : DefaultFontFamilyName;
    }

    internal static IReadOnlyList<string> BuildAvailableFontFamilyNames(IEnumerable<string> installedFontFamilyNames)
    {
        var installedByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string fontFamilyName in installedFontFamilyNames)
        {
            if (string.IsNullOrWhiteSpace(fontFamilyName))
            {
                continue;
            }

            string trimmed = fontFamilyName.Trim();
            installedByName.TryAdd(trimmed, trimmed);
        }

        var available = new List<string>();
        foreach (string preferred in PreferredFontFamilyNames)
        {
            if (installedByName.Remove(preferred, out string? actual))
            {
                available.Add(actual);
            }
        }

        foreach (string installed in installedByName.Values
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            available.Add(installed);
        }

        if (available.Count == 0)
        {
            available.Add(DefaultFontFamilyName);
        }

        return available;
    }
}
