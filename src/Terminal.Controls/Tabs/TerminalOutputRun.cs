using System.Windows.Media;

namespace Terminal.Tabs;

/// <summary>
/// One run of same-styled text in a captured command output row (see
/// <see cref="ShellCommandOutputEventArgs.StyledLines"/>). Colors are the resolved RGB the terminal drew with;
/// null means the terminal's default foreground/background.
/// </summary>
public sealed record TerminalOutputRun(
    string Text,
    Color? Foreground,
    Color? Background,
    bool Bold,
    bool Dim,
    bool Italic,
    bool Underline,
    bool Strikethrough,
    bool Inverse,
    string? Hyperlink)
{
    /// <summary>True when the run carries no styling at all (plain text in the default colors).</summary>
    public bool IsPlain =>
        Foreground is null && Background is null && !Bold && !Dim && !Italic && !Underline
        && !Strikethrough && !Inverse && Hyperlink is null;
}
