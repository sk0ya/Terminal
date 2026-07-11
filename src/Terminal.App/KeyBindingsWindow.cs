using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Terminal.Settings;

namespace Terminal;

internal sealed class KeyBindingsWindow : Window
{
    private readonly Dictionary<string, TextBox> _editors = new(StringComparer.OrdinalIgnoreCase);
    private readonly TextBlock _validation = new() { Foreground = Brushes.IndianRed, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _save = new() { Content = "Save", Width = 84, Height = 32, IsDefault = true };

    internal KeyBindingsWindow(IReadOnlyDictionary<string, string> bindings)
    {
        Title = "Key bindings";
        Width = 520;
        Height = 650;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x13, 0x13, 0x13));
        Foreground = Brushes.WhiteSmoke;
        Bindings = TerminalKeyBindingCatalog.Normalize(bindings);

        var rows = new StackPanel { Margin = new Thickness(18) };
        foreach ((string action, string defaultChord) in TerminalKeyBindingCatalog.Defaults)
        {
            var editor = new TextBox
            {
                Text = Bindings.GetValueOrDefault(action, defaultChord),
                Tag = action,
                Height = 28,
                Margin = new Thickness(0, 3, 0, 8),
                Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x0F, 0x0F)),
                Foreground = Brushes.WhiteSmoke,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A))
            };
            editor.TextChanged += (_, _) => ValidateBindings();
            _editors[action] = editor;
            rows.Children.Add(new TextBlock { Text = action, Foreground = Brushes.DarkGray });
            rows.Children.Add(editor);
        }

        var reset = new Button { Content = "Reset defaults", Width = 110, Height = 32, Margin = new Thickness(0, 0, 8, 0) };
        reset.Click += (_, _) => ResetDefaults();
        _save.Click += (_, _) => SaveAndClose();
        var cancel = new Button { Content = "Cancel", Width = 84, Height = 32, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(reset); buttons.Children.Add(cancel); buttons.Children.Add(_save);
        rows.Children.Add(_validation); rows.Children.Add(buttons);
        Content = new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        ValidateBindings();
    }

    internal Dictionary<string, string> Bindings { get; private set; }

    private void ResetDefaults()
    {
        foreach ((string action, string chord) in TerminalKeyBindingCatalog.Defaults) _editors[action].Text = chord;
    }

    private bool ValidateBindings()
    {
        var candidate = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string action, TextBox editor) in _editors)
        {
            if (!TerminalKeyBindingCatalog.TryNormalizeChord(editor.Text, out string chord))
            {
                _validation.Text = $"Invalid shortcut: {action}";
                _save.IsEnabled = false;
                return false;
            }
            candidate[action] = chord;
        }
        var conflicts = TerminalKeyBindingCatalog.FindConflicts(candidate);
        if (conflicts.Count != 0)
        {
            var conflict = conflicts.First();
            _validation.Text = $"{conflict.Key} is assigned to {string.Join(" and ", conflict.Value)}.";
            _save.IsEnabled = false;
            return false;
        }
        _validation.Text = string.Empty;
        _save.IsEnabled = true;
        return true;
    }

    private void SaveAndClose()
    {
        if (!ValidateBindings()) return;
        Bindings = _editors.ToDictionary(pair => pair.Key, pair =>
        {
            TerminalKeyBindingCatalog.TryNormalizeChord(pair.Value.Text, out string chord);
            return chord;
        }, StringComparer.OrdinalIgnoreCase);
        DialogResult = true;
    }
}
