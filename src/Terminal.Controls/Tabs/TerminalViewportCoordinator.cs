namespace Terminal.Tabs;

internal sealed class TerminalViewportCoordinator(double autoFollowThreshold)
{
    private bool _isAlternateScreenMode;

    public bool FollowOutput { get; private set; } = true;

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
