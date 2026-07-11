using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Terminal.Settings;

namespace Terminal;

internal sealed class ColorSchemeWindow : Window
{
    private readonly ComboBox _scheme = new() { ItemsSource = TerminalColorThemeCatalog.SchemeNames, Height = 30 };
    private readonly Dictionary<string, TextBox> _colors = [];
    private readonly TextBlock _error = new() { Foreground = Brushes.IndianRed };
    private readonly Border _preview = new() { Height = 52, Margin = new Thickness(0, 8, 0, 8), Padding = new Thickness(10) };
    private readonly Button _save = new() { Content = "Save", Width = 84, Height = 32, IsDefault = true };
    private readonly string[] _palette;

    internal ColorSchemeWindow(TerminalAppSettings settings)
    {
        Title = "Color scheme"; Width = 520; Height = 700;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x13, 0x13, 0x13)); Foreground = Brushes.WhiteSmoke;
        TerminalColorTheme initial = TerminalColorThemeCatalog.Resolve(settings);
        _palette = (settings.CustomAnsiPalette is { Length: 16 } ? settings.CustomAnsiPalette : initial.AnsiPalette.Select(TerminalColorThemeCatalog.Format)).ToArray();

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "Preset", Foreground = Brushes.DarkGray }); panel.Children.Add(_scheme);
        AddEditor(panel, "Foreground", settings.CustomForeground ?? TerminalColorThemeCatalog.Format(initial.Foreground));
        AddEditor(panel, "Background", settings.CustomBackground ?? TerminalColorThemeCatalog.Format(initial.Background));
        AddEditor(panel, "Cursor", settings.CustomCursorColor ?? TerminalColorThemeCatalog.Format(initial.Cursor));
        AddEditor(panel, "Selection", settings.CustomSelectionColor ?? TerminalColorThemeCatalog.Format(initial.SelectionBackground));
        for (int i = 0; i < 16; i++) AddEditor(panel, $"ANSI {i}", _palette[i]);
        panel.Children.Add(_preview); panel.Children.Add(_error);
        var cancel = new Button { Content = "Cancel", Width = 84, Height = 32, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(cancel); buttons.Children.Add(_save); panel.Children.Add(buttons);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _scheme.SelectedItem = TerminalColorThemeCatalog.SchemeNames.Contains(settings.ColorScheme) ? settings.ColorScheme : "Dark";
        _scheme.SelectionChanged += (_, _) => { LoadPresetIfNeeded(); Validate(); };
        _save.Click += (_, _) => { if (Validate()) DialogResult = true; };
        Validate();
    }

    internal void ApplyTo(TerminalAppSettings settings)
    {
        settings.ColorScheme = (string?)_scheme.SelectedItem ?? "Dark";
        settings.CustomForeground = _colors["Foreground"].Text;
        settings.CustomBackground = _colors["Background"].Text;
        settings.CustomCursorColor = _colors["Cursor"].Text;
        settings.CustomSelectionColor = _colors["Selection"].Text;
        settings.CustomAnsiPalette = Enumerable.Range(0, 16).Select(i => _colors[$"ANSI {i}"].Text).ToArray();
    }

    private void AddEditor(Panel panel, string name, string value)
    {
        var box = new TextBox { Text = value, Height = 27, Margin = new Thickness(0, 2, 0, 6), Background = Brushes.Black, Foreground = Brushes.WhiteSmoke };
        box.TextChanged += (_, _) => Validate(); _colors[name] = box;
        panel.Children.Add(new TextBlock { Text = name, Foreground = Brushes.DarkGray }); panel.Children.Add(box);
    }

    private void LoadPresetIfNeeded()
    {
        if (_scheme.SelectedItem as string == "Custom") return;
        TerminalColorTheme theme = _scheme.SelectedItem as string == "Light" ? TerminalColorThemeCatalog.Light : TerminalColorTheme.Default;
        _colors["Foreground"].Text = TerminalColorThemeCatalog.Format(theme.Foreground);
        _colors["Background"].Text = TerminalColorThemeCatalog.Format(theme.Background);
        _colors["Cursor"].Text = TerminalColorThemeCatalog.Format(theme.Cursor);
        _colors["Selection"].Text = TerminalColorThemeCatalog.Format(theme.SelectionBackground);
        for (int i = 0; i < 16; i++) _colors[$"ANSI {i}"].Text = TerminalColorThemeCatalog.Format(theme.AnsiPalette[i]);
    }

    private bool Validate()
    {
        if (_colors.Count < 20) return false;
        foreach ((string name, TextBox box) in _colors)
        {
            if (!TerminalColorThemeCatalog.TryParse(box.Text, out _)) { _error.Text = $"Invalid color: {name}"; _save.IsEnabled = false; return false; }
        }
        TerminalColorThemeCatalog.TryParse(_colors["Foreground"].Text, out Color foreground);
        TerminalColorThemeCatalog.TryParse(_colors["Background"].Text, out Color background);
        _preview.Background = new SolidColorBrush(background);
        _preview.Child = new TextBlock { Text = "Terminal color preview  Aa  123", Foreground = new SolidColorBrush(foreground), VerticalAlignment = VerticalAlignment.Center };
        _error.Text = string.Empty; _save.IsEnabled = true; return true;
    }
}
