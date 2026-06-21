using System.Windows.Controls;

namespace Terminal.Tabs;

/// <summary>
/// Event data for <see cref="TerminalTabView.ContextMenuBuilding"/>. Raised while the right-click
/// menu is opening, after the built-in Copy/Paste items. Hosts append their own entries to
/// <see cref="Menu"/> (typically acting on <see cref="SelectedText"/>). The menu only opens when a
/// selection exists, so <see cref="HasSelection"/> is always <c>true</c> here, but it is exposed for
/// symmetry and forward-compatibility.
/// </summary>
public sealed class TerminalContextMenuBuildingEventArgs : EventArgs
{
    internal TerminalContextMenuBuildingEventArgs(string selectedText, bool hasSelection, ContextMenu menu)
    {
        SelectedText = selectedText;
        HasSelection = hasSelection;
        Menu = menu;
    }

    /// <summary>The currently selected terminal text.</summary>
    public string SelectedText { get; }

    /// <summary>Whether the terminal has a non-empty selection.</summary>
    public bool HasSelection { get; }

    /// <summary>The menu being opened. Append <see cref="MenuItem"/>s or separators here; items added
    /// on a previous opening are removed automatically before this is raised.</summary>
    public ContextMenu Menu { get; }
}
