using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

using Terminal.Rendering;

namespace Terminal.Tabs;

/// <summary>
/// Scrollbar marks: ticks over the vertical scrollbar for each prompt, each failed command and
/// each find match, so a long scrollback shows where things are before scrolling to them.
/// </summary>
public partial class TerminalTabView
{
    private bool _isScrollMarkersEnabled = true;

    /// <summary>
    /// When true (default), the vertical scrollbar shows where commands started (OSC 133), which
    /// ones exited non-zero, and where the find panel's matches are.
    /// </summary>
    public bool IsScrollMarkersEnabled
    {
        get => _isScrollMarkersEnabled;
        set
        {
            if (_isScrollMarkersEnabled == value)
            {
                return;
            }

            _isScrollMarkersEnabled = value;
            UpdateScrollMarkers();
        }
    }

    /// <summary>The marks currently shown; test seam.</summary>
    internal IReadOnlyList<TerminalScrollMark> ScrollMarksForTests => CollectScrollMarks().ToList();

    private bool _scrollMarkersUpdateQueued;
    private object? _scrollMarkersInputs;

    /// <summary>
    /// Asks for the ticks to be redrawn. Requests are coalesced into one pass at background priority:
    /// streaming output changes the extent on every render, and each pass walks every mark and match.
    /// </summary>
    private void UpdateScrollMarkers()
    {
        if (_scrollMarkersUpdateQueued)
        {
            return;
        }

        _scrollMarkersUpdateQueued = true;
        _ = Dispatcher.BeginInvoke(UpdateScrollMarkersNow, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void UpdateScrollMarkersNow()
    {
        _scrollMarkersUpdateQueued = false;
        if (!_isScrollMarkersEnabled ||
            _terminalBuffer.IsAlternateScreenActive ||
            TerminalScrollHost.ComputedVerticalScrollBarVisibility != Visibility.Visible ||
            !TryGetScrollTrack(out double trackTop, out double trackHeight, out double barWidth))
        {
            ScrollMarkerBar.Visibility = Visibility.Collapsed;
            return;
        }

        var (_, charHeight) = MeasureCharacterCell();
        int totalLines = (int)Math.Round(TerminalScrollHost.ExtentHeight / Math.Max(charHeight, 1.0));
        // Nothing that places a tick changed: keep the ticks already drawn.
        var inputs = (_commandNavigation.Version, FindPopup.IsOpen, _findState.Matches, _findState.CurrentIndex,
            _foldMap, totalLines, trackTop, trackHeight, barWidth);
        if (ScrollMarkerBar.Visibility == Visibility.Visible && Equals(inputs, _scrollMarkersInputs))
        {
            return;
        }

        _scrollMarkersInputs = inputs;
        ScrollMarkerBar.Width = barWidth;
        ScrollMarkerBar.SetMarks(CollectScrollMarks(), totalLines, trackTop, trackHeight);
        ScrollMarkerBar.Visibility = Visibility.Visible;
    }

    private IEnumerable<TerminalScrollMark> CollectScrollMarks()
    {
        foreach (TerminalCommandMark command in _commandNavigation.Commands)
        {
            yield return new TerminalScrollMark(BufferToDisplayLine(command.PromptLine), TerminalScrollMarkKind.Prompt);
            // An empty submission after a failure still reports D;1 ($? stays false); only a command that ran failed.
            if (command.Executed && command.Done && command.ExitCode is { } code && code != 0)
            {
                yield return new TerminalScrollMark(BufferToDisplayLine(command.CommandLine), TerminalScrollMarkKind.FailedCommand);
            }
        }

        if (!FindPopup.IsOpen)
        {
            yield break;
        }

        TerminalMatch? current = _findState.CurrentMatch;
        foreach (TerminalMatch match in _findState.Matches)
        {
            yield return new TerminalScrollMark(match.LineIndex, TerminalScrollMarkKind.FindMatch);
        }

        if (current is { } selected)
        {
            yield return new TerminalScrollMark(selected.LineIndex, TerminalScrollMarkKind.CurrentFindMatch);
        }
    }

    /// <summary>
    /// The vertical scrollbar's track (the part between the arrow buttons) relative to the viewport
    /// host, read from the live template so a restyled scrollbar still lines up.
    /// </summary>
    private bool TryGetScrollTrack(out double trackTop, out double trackHeight, out double barWidth)
    {
        trackTop = 0;
        trackHeight = 0;
        barWidth = SystemParameters.VerticalScrollBarWidth;
        if (TerminalScrollHost.Template?.FindName("PART_VerticalScrollBar", TerminalScrollHost) is not ScrollBar bar ||
            bar.Track is not Track track ||
            track.ActualHeight <= 0)
        {
            return false;
        }

        try
        {
            trackTop = track.TranslatePoint(new Point(0, 0), TerminalViewportHost).Y;
        }
        catch (InvalidOperationException)
        {
            // Not in the same visual tree yet (template not applied to the host).
            return false;
        }

        trackHeight = track.ActualHeight;
        barWidth = bar.ActualWidth > 0 ? bar.ActualWidth : barWidth;
        return true;
    }
}
