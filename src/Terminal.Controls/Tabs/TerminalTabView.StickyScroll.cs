using System.Windows;
using System.Windows.Input;

namespace Terminal.Tabs;

/// <summary>
/// Sticky scroll: while the user reads back through the scrollback, the command line that
/// produced the output at the top of the viewport is pinned above it (OSC 133 prompt/command
/// boundaries), and clicking it scrolls back to that command.
/// </summary>
public partial class TerminalTabView
{
    private bool _isStickyScrollEnabled = true;
    private int? _stickyCommandLine;

    /// <summary>
    /// When true (default), the command whose output is at the top of the viewport is pinned
    /// above it once its own line has scrolled out of view; clicking the pinned line scrolls
    /// to that command. Needs OSC 133 shell integration; has no effect without it.
    /// </summary>
    public bool IsStickyScrollEnabled
    {
        get => _isStickyScrollEnabled;
        set
        {
            if (_isStickyScrollEnabled == value)
            {
                return;
            }

            _isStickyScrollEnabled = value;
            UpdateStickyCommand();
        }
    }

    /// <summary>The absolute line currently pinned by sticky scroll, or null when nothing is shown.</summary>
    internal int? StickyCommandLine => _stickyCommandLine;

    private void UpdateStickyCommand()
    {
        int? line = ResolveStickyCommandLine();
        if (line is not { } absoluteLine)
        {
            HideStickyCommand();
            return;
        }

        var (_, charHeight) = MeasureCharacterCell();
        if (_stickyCommandLine != absoluteLine)
        {
            _stickyCommandLine = absoluteLine;
            StickyCommandText.Text = _terminalBuffer
                .GetPlainTextForAbsoluteLineRange(absoluteLine, absoluteLine + 1)
                .TrimEnd();
        }

        // Re-read every time: the command can finish while it is pinned.
        StickyCommandMetaText.Text = FormatCommandMeta(_commandNavigation.FindOwner(absoluteLine));

        // Match the surface's font so the pinned line reads as the line it stands in for.
        StickyCommandText.FontFamily = TerminalOutput.FontFamily;
        StickyCommandText.FontSize = TerminalOutput.FontSize;
        StickyCommandText.Height = Math.Max(charHeight, 1.0);
        StickyCommandMetaText.FontFamily = TerminalOutput.FontFamily;
        StickyCommandMetaText.FontSize = TerminalOutput.FontSize;
        StickyCommandOverlay.Margin = new Thickness(
            0,
            0,
            TerminalScrollHost.ComputedVerticalScrollBarVisibility == Visibility.Visible
                ? SystemParameters.VerticalScrollBarWidth
                : 0,
            0);
        StickyCommandOverlay.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// <c>✓ 1.2s</c> / <c>✗ 2 · 1.2s</c> for a finished command, <c>…</c> while it still runs,
    /// empty when nothing is known (no D seen and no timing).
    /// </summary>
    internal static string FormatCommandMeta(TerminalCommandMark? mark)
    {
        if (mark is not { } command)
        {
            return string.Empty;
        }

        if (!command.Done)
        {
            return command.Executed ? "…" : string.Empty;
        }

        string status = command.ExitCode is { } code && code != 0 ? $"✗ {code}" : "✓";
        return command.Duration is { } duration
            ? $"{status} · {FormatCommandDuration(duration)}"
            : status;
    }

    private int? ResolveStickyCommandLine()
    {
        if (!_isStickyScrollEnabled ||
            _terminalBuffer.IsAlternateScreenActive ||
            !_commandNavigation.HasPrompts)
        {
            return null;
        }

        var (_, charHeight) = MeasureCharacterCell();
        int topLine = DisplayToBufferLine((int)(TerminalScrollHost.VerticalOffset / Math.Max(charHeight, 1.0)));
        return _commandNavigation.FindStickyCommandLine(topLine);
    }

    private void HideStickyCommand()
    {
        _stickyCommandLine = null;
        StickyCommandOverlay.Visibility = Visibility.Collapsed;
    }

    private void ResetStickyCommand()
    {
        HideStickyCommand();
        StickyCommandText.Text = string.Empty;
        StickyCommandMetaText.Text = string.Empty;
    }

    private void StickyCommandOverlay_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_stickyCommandLine is not { } line)
        {
            return;
        }

        e.Handled = true;
        ScrollToAbsoluteLine(line);
        UpdateStickyCommand();
        FocusTerminal();
    }
}
