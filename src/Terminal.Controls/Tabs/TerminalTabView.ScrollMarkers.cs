using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

using Terminal.Rendering;
using Terminal.Settings;

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

    private void UpdateScrollMarkers()
    {
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
        ScrollMarkerBar.Width = barWidth;
        ScrollMarkerBar.SetMarks(CollectScrollMarks(), totalLines, trackTop, trackHeight);
        ScrollMarkerBar.Visibility = Visibility.Visible;
    }

    private IEnumerable<TerminalScrollMark> CollectScrollMarks()
    {
        foreach (TerminalCommandMark command in _commandNavigation.Commands)
        {
            yield return new TerminalScrollMark(BufferToDisplayLine(command.PromptLine), TerminalScrollMarkKind.Prompt);
            if (command.Done && command.ExitCode is { } code && code != 0)
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

    private void ApplyScrollMarkerTheme(TerminalColorTheme theme)
    {
        Color prompt = theme.Foreground;
        prompt.A = 0x80;
        Color failed = theme.AnsiPalette.Count > 9 ? theme.AnsiPalette[9] : Colors.IndianRed;
        Color find = theme.AnsiPalette.Count > 11 ? theme.AnsiPalette[11] : Colors.Gold;
        find.A = 0xB0;
        Color currentFind = theme.AnsiPalette.Count > 11 ? theme.AnsiPalette[11] : Colors.Gold;
        ScrollMarkerBar.SetBrushes(prompt, find, failed, currentFind);
    }
}
