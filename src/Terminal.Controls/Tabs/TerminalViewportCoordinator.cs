namespace Terminal.Tabs;

internal sealed class TerminalViewportCoordinator(
    double autoFollowThreshold,
    short initialColumns = 120,
    short initialRows = 30)
{
    private bool _isAlternateScreenMode;
    private (short Columns, short Rows)? _sessionSize;

    public bool FollowOutput { get; private set; } = true;
    public short Columns { get; private set; } = initialColumns;
    public short Rows { get; private set; } = initialRows;

    public bool UpdateSize(short columns, short rows)
    {
        if (columns == Columns && rows == Rows)
        {
            return false;
        }

        Columns = columns;
        Rows = rows;
        return true;
    }

    /// <summary>The size the session being started was created with. The view's size and the
    /// session's size are tracked separately: creating a session can take a while (a resident
    /// host behind a pipe), and a layout change that lands meanwhile resizes only the view.</summary>
    public void SetSessionSize(short columns, short rows) => _sessionSize = (columns, rows);

    /// <summary>True when the session lags behind the view; the caller sends the returned size
    /// and it is recorded as the session's size.</summary>
    public bool TryTakeSessionResize(out short columns, out short rows)
    {
        columns = Columns;
        rows = Rows;
        if (_sessionSize == (Columns, Rows))
        {
            return false;
        }

        _sessionSize = (Columns, Rows);
        return true;
    }

    public bool SetAlternateScreenMode(bool active)
    {
        if (_isAlternateScreenMode == active)
        {
            return false;
        }

        _isAlternateScreenMode = active;
        return true;
    }

    public void UpdateFollowState(bool isAlternateScreenActive, double distanceFromBottom)
    {
        FollowOutput = isAlternateScreenActive || distanceFromBottom <= autoFollowThreshold;
    }

    public void StopFollowing() => FollowOutput = false;

    /// <summary>Follow the live screen again. Reading the scrollback stops the viewport from
    /// following, but sending input means the human is back at the prompt, so the next render
    /// must land on the cursor rather than leave the old output on screen.</summary>
    public void ResumeFollowing() => FollowOutput = true;

    public double ResolveRestoredVerticalOffset(
        bool isAlternateScreenActive,
        double preservedDistanceFromBottom,
        double extentHeight,
        double viewportHeight)
    {
        if (isAlternateScreenActive)
        {
            FollowOutput = true;
            return 0;
        }

        double maxOffset = Math.Max(0, extentHeight - viewportHeight);
        if (FollowOutput || preservedDistanceFromBottom <= autoFollowThreshold)
        {
            FollowOutput = true;
            return maxOffset;
        }

        return Math.Max(0, maxOffset - preservedDistanceFromBottom);
    }
}
