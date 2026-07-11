using System.Windows.Input;
using Terminal.Settings;

namespace Terminal.Input;

public sealed class TerminalKeyBindings
{
    private Dictionary<string, KeyChord> _bindings = [];

    public TerminalKeyBindings(IReadOnlyDictionary<string, string>? bindings = null) => Update(bindings);

    public void Update(IReadOnlyDictionary<string, string>? bindings)
    {
        _bindings = [];
        foreach ((string action, string chord) in TerminalKeyBindingCatalog.Normalize(bindings))
        {
            if (TryParse(chord, out KeyChord parsed)) _bindings[action] = parsed;
        }
    }

    public bool Matches(string action, Key key, ModifierKeys modifiers) =>
        _bindings.TryGetValue(action, out KeyChord chord) && chord.Key == key && chord.Modifiers == modifiers;

    internal static bool TryParse(string chord, out KeyChord result)
    {
        result = default;
        if (!TerminalKeyBindingCatalog.TryNormalizeChord(chord, out string normalized)) return false;
        string[] parts = normalized.Split('+');
        if (!Enum.TryParse(parts[^1], true, out Key key) || key == Key.None) return false;
        ModifierKeys modifiers = ModifierKeys.None;
        foreach (string part in parts[..^1])
        {
            modifiers |= part switch { "Ctrl" => ModifierKeys.Control, "Shift" => ModifierKeys.Shift, "Alt" => ModifierKeys.Alt, "Win" => ModifierKeys.Windows, _ => ModifierKeys.None };
        }
        result = new(key, modifiers);
        return true;
    }

    internal readonly record struct KeyChord(Key Key, ModifierKeys Modifiers);
}
