using System.Windows;
using System.Windows.Input;

namespace Terminal.Tabs;

/// <summary>
/// Hint mode (Quick Select, Ctrl+Shift+Space): URLs, paths, hashes and the like on screen get
/// short labels painted over them; typing a label copies that text, Shift+label also pastes it at
/// the prompt. Escape, or any other key, leaves the mode.
/// </summary>
public partial class TerminalTabView
{
    private TerminalHintSelection? _hintSelection;

    /// <summary>Whether hint mode is showing labels.</summary>
    public bool IsHintModeActive => _hintSelection is not null;

    /// <summary>
    /// Labels the copyable targets in the viewport. Returns false (and stays out of hint mode) when
    /// there is nothing to label.
    /// </summary>
    public bool EnterHintMode()
    {
        IReadOnlyList<TerminalHint> hints = TerminalHintFinder.Find(TerminalOutput.GetVisibleLineTexts());
        if (hints.Count == 0)
        {
            SetStatus("Nothing to select on screen.");
            return false;
        }

        CloseFindPanel();
        CloseHistoryPanel();
        _hintSelection = new TerminalHintSelection(hints);
        UpdateHintOverlay();
        return true;
    }

    /// <summary>Leaves hint mode without choosing anything.</summary>
    public void ExitHintMode()
    {
        if (_hintSelection is null)
        {
            return;
        }

        _hintSelection = null;
        TerminalOutput.SetHints([], string.Empty);
    }

    /// <summary>Hint mode owns the keyboard while it is up; every key is consumed.</summary>
    private void HandleHintKey(KeyEventArgs e, Key key, ModifierKeys modifiers)
    {
        e.Handled = true;
        if (_hintSelection is not { } selection)
        {
            return;
        }

        TerminalHintKeyResult result = key switch
        {
            Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or
                Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.System => TerminalHintKeyResult.Continue,
            Key.Back => selection.Backspace(),
            >= Key.A and <= Key.Z when (modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == 0 =>
                selection.TypeLetter((char)('a' + (key - Key.A)), shift: (modifiers & ModifierKeys.Shift) != 0),
            _ => TerminalHintKeyResult.Cancel
        };

        switch (result)
        {
            case TerminalHintKeyResult.Continue:
                UpdateHintOverlay();
                break;
            case TerminalHintKeyResult.Cancel:
                ExitHintMode();
                break;
            default:
                TerminalHint chosen = selection.Chosen!.Value;
                ExitHintMode();
                UseHint(chosen.Text, paste: result == TerminalHintKeyResult.CopyAndPaste);
                break;
        }
    }

    private void UpdateHintOverlay()
    {
        if (_hintSelection is { } selection)
        {
            TerminalOutput.SetHints(selection.Remaining.ToList(), selection.Typed);
        }
    }

    private void UseHint(string text, bool paste)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            SetStatus($"Clipboard update failed: {ex.Message}");
            return;
        }

        SetStatus($"Copied {text}");
        if (paste && _session is not null)
        {
            TerminalPasteAction action = _clipboardState.ResolvePaste(
                hasSession: true,
                clipboardContainsText: true,
                text,
                _terminalBuffer.BracketedPasteEnabled,
                multilinePasteApproved: true);
            if (action is { Kind: TerminalPasteActionKind.Send, Text: not null })
            {
                _ = SendUserInput(action.Text);
            }
        }
    }
}
