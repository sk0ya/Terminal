namespace Terminal.Tabs;

/// <summary>
/// When one output read holds both the end of a synchronized frame (?2026l) and the start of the next
/// (?2026h), find where to cut so the finished frame is drawn before the next one is parsed. Processing
/// the whole read at once leaves the buffer inside the new frame, so the finished one stayed off screen
/// until the next ?2026l or the 250ms watchdog (Claude Code's Alt+Enter newline showed up late).
/// The parser carries partial sequences across chunks, so cutting anywhere never changes the meaning.
/// </summary>
internal static class TerminalSynchronizedUpdateSplitter
{
    internal const string Begin = "\u001b[?2026h";
    internal const string End = "\u001b[?2026l";

    /// <summary>
    /// The index just after the last ?2026l, but only when a ?2026h follows it; otherwise -1 (cutting
    /// would not change what gets drawn).
    /// </summary>
    public static int FindSplitAfterCompletedFrame(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return -1;
        }

        int end = text.LastIndexOf(End, StringComparison.Ordinal);
        if (end < 0)
        {
            return -1;
        }

        int split = end + End.Length;
        return split < text.Length && text.IndexOf(Begin, split, StringComparison.Ordinal) >= 0
            ? split
            : -1;
    }
}
