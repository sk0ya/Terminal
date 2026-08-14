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

        string normalized = NormalizeNewlines(text);
        bool isMultiline = normalized.Contains('\r');
        if (!bracketedPasteEnabled && isMultiline && !multilinePasteApproved)
        {
            return new(TerminalPasteActionKind.ConfirmMultiline);
        }

        string payload = bracketedPasteEnabled
            ? $"\u001b[200~{normalized}\u001b[201~"
            : normalized;
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

    /// <summary>
    /// Collapses every line break in pasted text to a single CR, the byte a terminal sends for
    /// Enter.
    /// </summary>
    /// <remarks>
    /// The Windows clipboard carries CRLF. Forwarding both bytes makes applications that treat
    /// CR and LF as separate line breaks - vim, or readline under WSL - insert a blank line for
    /// every pasted line, so a paste is normalized here rather than passed through verbatim.
    /// </remarks>
    private static string NormalizeNewlines(string text)
    {
        if (!text.Contains('\n'))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        for (int index = 0; index < text.Length; index++)
        {
            char ch = text[index];
            if (ch == '\r')
            {
                builder.Append('\r');
                // Consume the LF of a CRLF pair; a lone CR is already in the wanted form.
                if (index + 1 < text.Length && text[index + 1] == '\n')
                {
                    index++;
                }

                continue;
            }

            builder.Append(ch == '\n' ? '\r' : ch);
        }

        return builder.ToString();
    }
}
