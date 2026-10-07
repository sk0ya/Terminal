using System.Windows;

namespace Terminal.Tabs;

/// <summary>Event data for <see cref="TerminalTabView.UserInputSent"/>.</summary>
public sealed class TerminalUserInputEventArgs : EventArgs
{
    internal TerminalUserInputEventArgs(string text)
    {
        Text = text;
    }

    /// <summary>The bytes sent to the session, as text (already encoded for this pane's input modes).</summary>
    public string Text { get; }
}

/// <summary>
/// Input broadcast support: the tab reports what the human typed or pasted, and shows a frame
/// while the host is mirroring that input to other panes.
/// </summary>
public partial class TerminalTabView
{
    /// <summary>
    /// Raised after input the user typed or pasted (keys, text, paste, Ctrl+C from the menu) was
    /// written to the session. Not raised for terminal replies, mouse or focus reports, or input sent
    /// through <see cref="SendTerminalInput(string)"/> — so a host can mirror it to other panes with
    /// <see cref="SendMirroredInput(string)"/> without echoing back.
    /// </summary>
    public event EventHandler<TerminalUserInputEventArgs>? UserInputSent;

    /// <summary>Shows a frame around the terminal to say its input is being broadcast to other panes.</summary>
    public bool IsInputBroadcastIndicatorVisible
    {
        get => BroadcastIndicatorOverlay.Visibility == Visibility.Visible;
        set => BroadcastIndicatorOverlay.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Sends input mirrored from another pane: written like typed input (the view returns to the
    /// live screen) but without raising <see cref="UserInputSent"/>, so mirroring never echoes back.
    /// </summary>
    public bool SendMirroredInput(string text)
    {
        if (!SendTerminalInput(text))
        {
            return false;
        }

        ScrollToLiveScreen();
        return true;
    }

    private void RaiseUserInputSent(string text) =>
        UserInputSent?.Invoke(this, new TerminalUserInputEventArgs(text));
}
