namespace Terminal.Buffer;

internal enum DcsCommandKind
{
    Unknown,
    Decrqss,
    XtGetTcap,
    XtSetTcap,
    Sixel
}

internal readonly record struct DcsCommand(
    DcsCommandKind Kind,
    string? RequestToken = null,
    string? Payload = null);

internal static class DcsDecoder
{
    public static DcsCommand Decode(string content)
    {
        if (content.StartsWith("$q", StringComparison.Ordinal))
        {
            return new DcsCommand(DcsCommandKind.Decrqss, content[2..]);
        }

        if (content.StartsWith("+q", StringComparison.Ordinal))
        {
            return new DcsCommand(DcsCommandKind.XtGetTcap, Payload: content[2..]);
        }

        if (content.StartsWith("+p", StringComparison.Ordinal))
        {
            return new DcsCommand(DcsCommandKind.XtSetTcap, Payload: content[2..]);
        }

        int introducerIndex = content.IndexOf('q');
        if (introducerIndex >= 0 && IsSixelIntroducer(content, introducerIndex))
        {
            return new DcsCommand(DcsCommandKind.Sixel);
        }

        return default;
    }

    private static bool IsSixelIntroducer(string content, int introducerIndex)
    {
        for (int index = 0; index < introducerIndex; index++)
        {
            char ch = content[index];
            if (ch is not ((>= '0' and <= '9') or ';'))
            {
                return false;
            }
        }

        return true;
    }
}
