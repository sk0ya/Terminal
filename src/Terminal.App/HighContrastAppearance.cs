using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Terminal;

internal static class HighContrastAppearance
{
    private static readonly ConditionalWeakTable<Window, State> States = new();

    internal static void Apply(Window window, bool active)
    {
        if (!active)
        {
            if (States.TryGetValue(window, out State? state)) state.Restore();
            States.Remove(window);
            return;
        }

        State current = States.GetOrCreateValue(window);
        Set(current, window, Control.BackgroundProperty, SystemColors.WindowBrush);
        Set(current, window, Control.ForegroundProperty, SystemColors.WindowTextBrush);
        Apply(current, window.Content as DependencyObject);
    }

    private static void Apply(State state, DependencyObject? element)
    {
        if (element is null) return;
        if (element is TextBlock)
            Set(state, element, TextBlock.ForegroundProperty, SystemColors.WindowTextBrush);
        if (element is Control control)
        {
            Set(state, control, Control.ForegroundProperty, SystemColors.WindowTextBrush);
            if (control is TextBox or ComboBox or ListBox or ListView)
                Set(state, control, Control.BackgroundProperty, SystemColors.WindowBrush);
        }
        if (element is Border border)
        {
            Set(state, border, Border.BorderBrushProperty, SystemColors.WindowTextBrush);
            if (border.Background is SolidColorBrush { Color.A: > 0 })
                Set(state, border, Border.BackgroundProperty, SystemColors.WindowBrush);
        }
        int count = VisualTreeHelper.GetChildrenCount(element);
        for (int i = 0; i < count; i++) Apply(state, VisualTreeHelper.GetChild(element, i));
    }

    private static void Set(State state, DependencyObject target, DependencyProperty property, object value)
    {
        state.Remember(target, property);
        target.SetValue(property, value);
    }

    private sealed class State
    {
        private readonly List<Entry> _entries = [];
        private readonly HashSet<(DependencyObject, DependencyProperty)> _seen = [];
        internal void Remember(DependencyObject target, DependencyProperty property)
        {
            if (_seen.Add((target, property))) _entries.Add(new(target, property, target.ReadLocalValue(property)));
        }
        internal void Restore()
        {
            foreach (Entry entry in _entries)
                if (entry.Value == DependencyProperty.UnsetValue) entry.Target.ClearValue(entry.Property);
                else entry.Target.SetValue(entry.Property, entry.Value);
        }
    }
    private sealed record Entry(DependencyObject Target, DependencyProperty Property, object Value);
}
