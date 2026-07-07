using System.Text;

namespace Terminal.Tabs;

internal enum TerminalPasteActionKind { Ignore, ConfirmMultiline, Send }

internal sealed record TerminalPasteAction(TerminalPasteActionKind Kind, string? Text = null);

internal sealed class TerminalClipboardCoordinator
{
    public bool CanPaste(bool hasSession, bool clipboardContainsText) =>
        hasSession && clipboardContainsText;

    public TerminalPasteAction ResolvePaste(
        bool hasSession,
        bool clipboardContainsText,
        string text,
        bool bracketedPasteEnabled,
        bool multilinePasteApproved)
    {
        if (!CanPaste(hasSession, clipboardContainsText))
        {
            return new(TerminalPasteActionKind.Ignore);
        }

        bool isMultiline = text.Contains('\n') || text.Contains('\r');
        if (!bracketedPasteEnabled && isMultiline && !multilinePasteApproved)
        {
            return new(TerminalPasteActionKind.ConfirmMultiline);
        }

        string payload = bracketedPasteEnabled
            ? $"\u001b[200~{text}\u001b[201~"
            : text;
        return new(TerminalPasteActionKind.Send, payload);
    }

    public string BuildOsc52Response(string? selectionTargets, string text)
    {
        string targets = string.IsNullOrWhiteSpace(selectionTargets)
            ? "c"
            : selectionTargets.Trim();
        string encodedText = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        return $"\u001b]52;{targets};{encodedText}\u0007";
    }
}
