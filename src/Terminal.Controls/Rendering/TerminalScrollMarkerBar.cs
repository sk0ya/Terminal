using System.Windows;
using System.Windows.Media;

namespace Terminal.Rendering;

/// <summary>What a scrollbar mark stands for; later kinds draw over earlier ones on the same row.</summary>
internal enum TerminalScrollMarkKind
{
    /// <summary>A prompt (OSC 133;A): where each command starts.</summary>
    Prompt,

    /// <summary>A find match.</summary>
    FindMatch,

    /// <summary>A command that finished with a non-zero exit code.</summary>
    FailedCommand,

    /// <summary>The find match currently selected.</summary>
    CurrentFindMatch
}

internal readonly record struct TerminalScrollMark(int Line, TerminalScrollMarkKind Kind);

/// <summary>One tick to draw: its vertical centre in the bar and the strongest kind that landed there.</summary>
internal readonly record struct TerminalScrollMarkTick(double Y, TerminalScrollMarkKind Kind);

/// <summary>
/// Ticks drawn over the vertical scrollbar's track: where commands started, which ones failed, and
/// where the find matches are. It does not take input; the scrollbar under it keeps working.
/// </summary>
internal sealed class TerminalScrollMarkerBar : FrameworkElement
{
    private const double TickHeight = 2;

    // Fixed mid-luminance colours: the bar sits on the system scrollbar, which may be light or
    // dark whatever the terminal theme is, so theme colours (light text on a light track) vanish.
    private static readonly Brush PromptBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x3D, 0x8B, 0xFD)));
    private static readonly Brush FindBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE5, 0xA5, 0x0A)));
    private static readonly Brush FailedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE5, 0x53, 0x4B)));
    private static readonly Brush CurrentFindBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x6A, 0x00)));

    private IReadOnlyList<TerminalScrollMarkTick> _ticks = [];

    public TerminalScrollMarkerBar()
    {
        IsHitTestVisible = false;
        Focusable = false;
        SnapsToDevicePixels = true;
    }

    internal IReadOnlyList<TerminalScrollMarkTick> Ticks => _ticks;

    /// <summary>
    /// Places <paramref name="marks"/> on a track that starts <paramref name="trackTop"/> pixels down
    /// the bar and is <paramref name="trackHeight"/> tall, for a buffer of <paramref name="totalLines"/>.
    /// </summary>
    public void SetMarks(IEnumerable<TerminalScrollMark> marks, int totalLines, double trackTop, double trackHeight)
    {
        _ticks = Layout(marks, totalLines, trackTop, trackHeight);
        InvalidateVisual();
    }

    /// <summary>
    /// Maps each mark to the pixel row of the track that its line falls on, keeping one tick per
    /// row (a long scrollback puts many lines on a row) with the strongest kind winning.
    /// </summary>
    internal static IReadOnlyList<TerminalScrollMarkTick> Layout(
        IEnumerable<TerminalScrollMark> marks, int totalLines, double trackTop, double trackHeight)
    {
        if (totalLines <= 0 || trackHeight <= TickHeight)
        {
            return [];
        }

        var rows = new SortedDictionary<int, TerminalScrollMarkKind>();
        foreach (TerminalScrollMark mark in marks)
        {
            if (mark.Line < 0 || mark.Line >= totalLines)
            {
                continue;
            }

            double centre = (mark.Line + 0.5) / totalLines * trackHeight;
            int row = (int)Math.Clamp(Math.Floor(centre / TickHeight), 0, (trackHeight / TickHeight) - 1);
            if (!rows.TryGetValue(row, out TerminalScrollMarkKind existing) || mark.Kind > existing)
            {
                rows[row] = mark.Kind;
            }
        }

        return rows
            .Select(pair => new TerminalScrollMarkTick(trackTop + (pair.Key * TickHeight) + (TickHeight / 2), pair.Value))
            .ToList();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        double width = ActualWidth;
        if (width <= 0)
        {
            return;
        }

        foreach (TerminalScrollMarkTick tick in _ticks)
        {
            // Prompts are a short notch on the left edge so they never hide the thumb; the
            // rest span the bar.
            (Brush brush, double left, double right) = tick.Kind switch
            {
                TerminalScrollMarkKind.Prompt => (PromptBrush, 0.0, width * 0.5),
                TerminalScrollMarkKind.FindMatch => (FindBrush, width * 0.5, width),
                TerminalScrollMarkKind.FailedCommand => (FailedBrush, 0.0, width),
                _ => (CurrentFindBrush, 0.0, width)
            };
            drawingContext.DrawRectangle(brush, null, new Rect(left, tick.Y - (TickHeight / 2), right - left, TickHeight));
        }
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
