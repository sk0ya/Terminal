using System.Windows;
using System.Windows.Interop;
using Terminal.Sessions;

namespace Terminal.Tabs;

/// <summary>
/// Gives the session's hidden console window an owner: the window this view is shown in. Programs that attach a
/// dialog to their console (Git Credential Manager, <c>Connect-AzAccount</c>'s sign-in picker) then open it in front
/// of that window instead of behind it, where the command just looks stuck. Windows Terminal and VS Code 1.144 do
/// the same. Re-applied when a session starts and whenever the view is loaded again (moved into a detached window).
/// </summary>
public partial class TerminalTabView
{
    private IntPtr _consoleOwnerWindow;
    private ITerminalSession? _consoleOwnerSession;

    /// <summary>
    /// Whether to make the window showing this view the owner of the session's console window (default on).
    /// Only the bundled ConPTY supports it; with the inbox one this does nothing.
    /// </summary>
    public bool ReparentConsoleWindow { get; set; } = true;

    private void ApplyConsoleOwnerWindow()
    {
        if (!ReparentConsoleWindow || _session is not { } session)
        {
            return;
        }

        if (PresentationSource.FromVisual(this) is not HwndSource { Handle: var handle } || handle == IntPtr.Zero)
        {
            return;
        }

        if (handle == _consoleOwnerWindow && ReferenceEquals(session, _consoleOwnerSession))
        {
            return;
        }

        try
        {
            session.SetOwnerWindow(handle);
            _consoleOwnerWindow = handle;
            _consoleOwnerSession = session;
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            // The session ended between the check and the call; the next start applies it again.
        }
    }
}
