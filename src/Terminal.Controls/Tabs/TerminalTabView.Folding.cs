using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

using Terminal.Buffer;

namespace Terminal.Tabs;

/// <summary>
/// Command output folding: a finished command's output can be collapsed to one summary line and
/// expanded again by clicking it. Only the part already in the scrollback folds — the live screen
/// belongs to ConPTY, and hiding rows there would put the cursor and mouse reports on the wrong
/// line. Fold ends are pinned when the fold is made, so later output never joins a fold.
/// </summary>
public partial class TerminalTabView
{
    private readonly List<(TerminalLineMark Start, TerminalLineMark End)> _foldMarks = [];
    private TerminalFoldMap _foldMap = TerminalFoldMap.Empty;
    private int? _contextMenuFoldLine;
    private TerminalFold? _contextMenuUnfold;

    /// <summary>How many command outputs are folded right now.</summary>
    public int FoldCount => _foldMap.Folds.Count;

    /// <summary>
    /// Folds the output of the command that owns <paramref name="bufferLine"/> (OSC 133 shell
    /// integration), as far as it has scrolled into the scrollback. Returns false, with a status
    /// message, when there is no finished command there or nothing of its output is off-screen yet.
    /// </summary>
    internal bool FoldCommandOutputAt(int bufferLine)
    {
        if (ResolveFoldRange(bufferLine) is not { } fold)
        {
            SetStatus("Nothing to fold here: the command's output is still on the live screen.");
            return false;
        }

        _foldMarks.Add((_terminalBuffer.MarkAbsoluteLine(fold.Start), _terminalBuffer.MarkAbsoluteLine(fold.End)));
        OnFoldsChanged();
        SetStatus($"Folded {fold.LineCount} lines.");
        return true;
    }

    /// <summary>Expands every folded command output.</summary>
    public void UnfoldAll()
    {
        if (_foldMarks.Count == 0)
        {
            return;
        }

        _foldMarks.Clear();
        OnFoldsChanged();
    }

    /// <summary>The range a fold at <paramref name="bufferLine"/> would hide, or null when nothing can fold.</summary>
    internal TerminalFold? ResolveFoldRange(int bufferLine)
    {
        if (_terminalBuffer.IsAlternateScreenActive ||
            _commandNavigation.FindOwner(bufferLine) is not { Executed: true } owner)
        {
            return null;
        }

        int end = _terminalBuffer.ScrollbackLineCount;
        foreach (int prompt in _commandNavigation.PromptLines)
        {
            if (prompt > owner.PromptLine && prompt < end)
            {
                end = prompt;
            }
        }

        var fold = new TerminalFold(owner.CommandLine + 1, end);
        bool overlaps = _foldMap.Folds.Any(existing => fold.Start < existing.End && existing.Start < fold.End);
        return fold.LineCount >= 2 && !overlaps ? fold : null;
    }

    private void UnfoldAtBufferLine(int bufferStart)
    {
        int removed = _foldMarks.RemoveAll(mark =>
            _terminalBuffer.TryResolveMark(mark.Start, out int start) && start == bufferStart);
        if (removed > 0)
        {
            OnFoldsChanged();
        }
    }

    private void OnFoldsChanged()
    {
        // The viewport normally keeps its distance from the bottom, so folding above it would slide
        // the text the user is looking at. Keep the top line where it is instead (unless following
        // live output, where the bottom is what matters).
        int? topBufferLine = null;
        if (!_viewportState.FollowOutput)
        {
            var (_, charHeight) = MeasureCharacterCell();
            topBufferLine = DisplayToBufferLine((int)(TerminalScrollHost.VerticalOffset / Math.Max(charHeight, 1.0)));
        }

        RequestDocumentRender(immediate: true);
        if (topBufferLine is { } line)
        {
            _ = Dispatcher.BeginInvoke(() => ScrollToAbsoluteLine(line), System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private void ResetFolds()
    {
        _foldMarks.Clear();
        _foldMap = TerminalFoldMap.Empty;
    }

    /// <summary>
    /// Resolves the fold marks against the buffer for this frame. A fold whose lines were evicted or
    /// renumbered is dropped; one that is not wholly in the scrollback is left out of this frame.
    /// </summary>
    private TerminalFoldMap BuildFoldMap()
    {
        if (_foldMarks.Count == 0 || _terminalBuffer.IsAlternateScreenActive)
        {
            return TerminalFoldMap.Empty;
        }

        int scrollback = _terminalBuffer.ScrollbackLineCount;
        var folds = new List<TerminalFold>(_foldMarks.Count);
        _foldMarks.RemoveAll(mark =>
        {
            if (!_terminalBuffer.TryResolveMark(mark.Start, out int start) ||
                !_terminalBuffer.TryResolveMark(mark.End, out int end))
            {
                return true;
            }

            if (end <= scrollback)
            {
                folds.Add(new TerminalFold(start, end));
            }

            return false;
        });
        return new TerminalFoldMap(folds);
    }

    /// <summary>The snapshot the surface shows: each fold replaced by its summary line.</summary>
    private AnsiTerminalBuffer.TerminalRenderSnapshot ApplyFolds(AnsiTerminalBuffer.TerminalRenderSnapshot snapshot)
    {
        _foldMap = BuildFoldMap();
        if (_foldMap.IsEmpty)
        {
            return snapshot;
        }

        Color foreground = _colorTheme.Foreground;
        Color dim = Color.FromArgb(0xFF, (byte)((foreground.R + _colorTheme.Background.R) / 2),
            (byte)((foreground.G + _colorTheme.Background.G) / 2), (byte)((foreground.B + _colorTheme.Background.B) / 2));
        return snapshot with
        {
            Lines = _foldMap.Apply(snapshot.Lines, fold => CreateFoldSummaryLine(fold, dim, _colorTheme.Background))
        };
    }

    // ASCII only, so its cell count is its length whatever the ambiguous-width setting.
    internal static string FormatFoldSummary(int lineCount) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"  [+] {lineCount:N0} lines folded (click to unfold)");

    private static AnsiTerminalBuffer.TerminalRenderLineSnapshot CreateFoldSummaryLine(TerminalFold fold, Color foreground, Color background)
    {
        string text = FormatFoldSummary(fold.LineCount);
        int cells = text.Length;
        return new AnsiTerminalBuffer.TerminalRenderLineSnapshot(
            cells,
            [
                new AnsiTerminalBuffer.TerminalRenderSegmentSnapshot(
                    text,
                    cells,
                    foreground,
                    background,
                    Bold: false,
                    Italic: true,
                    UnderlineStyle.None,
                    UnderlineColor: null,
                    Strikethrough: false,
                    Overline: false,
                    Hyperlink: null)
            ]);
    }

    /// <summary>Display line (what the surface shows) → buffer line.</summary>
    private int DisplayToBufferLine(int displayLine) => _foldMap.ToBuffer(displayLine);

    /// <summary>Buffer line → display line.</summary>
    private int BufferToDisplayLine(int bufferLine) => _foldMap.ToDisplay(bufferLine);

    /// <summary>A left click on a fold's summary line expands it. Returns whether the click was taken.</summary>
    private bool TryUnfoldAtPoint(MouseButtonEventArgs e)
    {
        if (_foldMap.IsEmpty ||
            e.ChangedButton != MouseButton.Left ||
            !TerminalOutput.TryGetTextPositionFromPoint(e.GetPosition(TerminalOutput), out int displayLine, out _))
        {
            return false;
        }

        _foldMap.ToBuffer(displayLine, out TerminalFold? summary);
        if (summary is not { } fold)
        {
            return false;
        }

        UnfoldAtBufferLine(fold.Start);
        e.Handled = true;
        return true;
    }

    /// <summary>Sets up the Fold / Unfold menu items for a right click at <paramref name="point"/>.</summary>
    private bool PrepareFoldMenuItems(Point point)
    {
        _contextMenuFoldLine = null;
        _contextMenuUnfold = null;
        if (TerminalOutput.TryGetTextPositionFromPoint(point, out int displayLine, out _))
        {
            int bufferLine = _foldMap.ToBuffer(displayLine, out TerminalFold? summary);
            if (summary is { } fold)
            {
                _contextMenuUnfold = fold;
            }
            else if (ResolveFoldRange(bufferLine) is not null)
            {
                _contextMenuFoldLine = bufferLine;
            }
        }

        FoldCommandOutputMenuItem.Visibility = _contextMenuFoldLine is null ? Visibility.Collapsed : Visibility.Visible;
        UnfoldMenuItem.Visibility = _contextMenuUnfold is null ? Visibility.Collapsed : Visibility.Visible;
        UnfoldAllMenuItem.Visibility = _foldMarks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        return _contextMenuFoldLine is not null || _contextMenuUnfold is not null || _foldMarks.Count > 0;
    }

    private void FoldCommandOutputMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_contextMenuFoldLine is { } line)
        {
            FoldCommandOutputAt(line);
        }

        QueueTerminalInputFocus();
    }

    private void UnfoldMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_contextMenuUnfold is { } fold)
        {
            UnfoldAtBufferLine(fold.Start);
        }

        QueueTerminalInputFocus();
    }

    private void UnfoldAllMenuItem_Click(object sender, RoutedEventArgs e)
    {
        UnfoldAll();
        QueueTerminalInputFocus();
    }

    /// <summary>The current display/buffer mapping; test seam.</summary>
    internal TerminalFoldMap FoldMapForTests => _foldMap;

    /// <summary>Rebuilds the fold map as a render would; test seam.</summary>
    internal void RefreshFoldsForTests() =>
        _ = ApplyFolds(_terminalBuffer.CreateRenderSnapshot(showCursor: false));
}
