namespace Terminal.Settings;

public static class TerminalKeyBindingCatalog
{
    public static IReadOnlyDictionary<string, string> Defaults { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["NewTab"] = "Ctrl+Shift+T",
            ["NewTabHere"] = "Ctrl+Shift+D",
            ["CloseTab"] = "Ctrl+Shift+W",
            ["NextTab"] = "Ctrl+Tab",
            ["PreviousTab"] = "Ctrl+Shift+Tab",
            ["OpenSettings"] = "Ctrl+OemComma",
            ["Copy"] = "Ctrl+Shift+C",
            ["Paste"] = "Ctrl+Shift+V",
            ["Find"] = "Ctrl+Shift+F",
            ["History"] = "Ctrl+R",
            ["PreviousCommand"] = "Ctrl+Shift+Up",
            ["NextCommand"] = "Ctrl+Shift+Down",
            ["SaveTranscript"] = "Ctrl+Shift+S",
            ["Restart"] = "Ctrl+Shift+R",
            ["IncreaseFontSize"] = "Ctrl+OemPlus",
            ["DecreaseFontSize"] = "Ctrl+OemMinus",
            ["ResetFontSize"] = "Ctrl+D0",
            ["SplitHorizontal"] = "Ctrl+Shift+H",
            ["SplitVertical"] = "Ctrl+Shift+E",
            ["ClosePane"] = "Ctrl+Shift+Q",
            ["NextPane"] = "Ctrl+Alt+Right",
            ["PreviousPane"] = "Ctrl+Alt+Left"
        };

    public static Dictionary<string, string> CreateDefaults() =>
        new(Defaults, StringComparer.OrdinalIgnoreCase);

    public static Dictionary<string, string> Normalize(IReadOnlyDictionary<string, string>? bindings)
    {
        Dictionary<string, string> result = CreateDefaults();
        if (bindings is null) return result;
        foreach ((string action, string chord) in bindings)
        {
            if (Defaults.ContainsKey(action) && TryNormalizeChord(chord, out string normalized))
            {
                result[action] = normalized;
            }
        }
        return result;
    }

    public static bool TryNormalizeChord(string? chord, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(chord)) return false;
        string[] parts = chord.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        var modifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < parts.Length - 1; i++)
        {
            string modifier = parts[i] switch
            {
                var value when value.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || value.Equals("Control", StringComparison.OrdinalIgnoreCase) => "Ctrl",
                var value when value.Equals("Shift", StringComparison.OrdinalIgnoreCase) => "Shift",
                var value when value.Equals("Alt", StringComparison.OrdinalIgnoreCase) => "Alt",
                var value when value.Equals("Win", StringComparison.OrdinalIgnoreCase) || value.Equals("Windows", StringComparison.OrdinalIgnoreCase) => "Win",
                _ => string.Empty
            };
            if (modifier.Length == 0 || !modifiers.Add(modifier)) return false;
        }
        string key = parts[^1];
        if (key.Length == 0 || key.Any(char.IsWhiteSpace)) return false;
        string[] order = ["Ctrl", "Shift", "Alt", "Win"];
        normalized = string.Join('+', order.Where(modifiers.Contains).Append(key));
        return true;
    }

    public static IReadOnlyDictionary<string, string[]> FindConflicts(IReadOnlyDictionary<string, string> bindings) =>
        bindings
            .Where(pair => TryNormalizeChord(pair.Value, out _))
            .GroupBy(pair => { TryNormalizeChord(pair.Value, out string chord); return chord; }, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .ToDictionary(group => group.Key, group => group.Select(pair => pair.Key).ToArray(), StringComparer.OrdinalIgnoreCase);
}
