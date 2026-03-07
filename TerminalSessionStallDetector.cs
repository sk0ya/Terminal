namespace ConPtyTerminal;

internal static class TerminalSessionStallDetector
{
    public static bool IsStartupStalled(
        bool hasOutput,
        DateTime startedAtUtc,
        DateTime nowUtc,
        TimeSpan initialOutputTimeout)
    {
        if (hasOutput)
        {
            return false;
        }

        return nowUtc - startedAtUtc > initialOutputTimeout;
    }
}
