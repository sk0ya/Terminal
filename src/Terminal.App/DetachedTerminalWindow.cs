using System.Windows;

using Terminal.Tabs;

namespace Terminal;

internal sealed class DetachedTerminalWindow : Window
{
    private readonly IReadOnlyList<TerminalTabView> _panes;
    private bool _panesClosed;

    internal DetachedTerminalWindow(
        UIElement content,
        IReadOnlyList<TerminalTabView> panes,
        string title,
        Window owner)
    {
        _panes = panes;
        if (owner.IsVisible)
        {
            Owner = owner;
        }
        Content = content;
        Title = FormatTitle(title);
        Width = Math.Max(owner.ActualWidth, 900);
        Height = Math.Max(owner.ActualHeight, 600);
        MinWidth = 500;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = true;
        Background = System.Windows.Media.Brushes.Black;
        HeaderTitleChangedSubscriptions();
        Closed += OnClosed;
    }

    internal IReadOnlyList<TerminalTabView> Panes => _panes;

    internal async Task ClosePanesAsync()
    {
        if (_panesClosed)
        {
            return;
        }

        _panesClosed = true;
        foreach (TerminalTabView pane in _panes)
        {
            await pane.CloseAsync();
        }
    }

    private void HeaderTitleChangedSubscriptions()
    {
        foreach (TerminalTabView pane in _panes)
        {
            pane.HeaderTitleChanged += Pane_HeaderTitleChanged;
        }
    }

    private void Pane_HeaderTitleChanged(object? sender, string title)
    {
        Title = FormatTitle(title);
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        foreach (TerminalTabView pane in _panes)
        {
            pane.HeaderTitleChanged -= Pane_HeaderTitleChanged;
        }

        await ClosePanesAsync();
    }

    private static string FormatTitle(string title) =>
        string.IsNullOrWhiteSpace(title) ? "ConPTY Terminal" : $"{title} - ConPTY Terminal";
}
