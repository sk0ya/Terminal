using System.Windows;

namespace Terminal.Tabs;

/// <summary>
/// "Copy Command Output": the outputs cut by <see cref="TerminalCommandOutputCoordinator"/> are kept
/// (bounded) so the latest one can be copied with a key and any earlier one from the right-click
/// menu over it. The cut ranges are reused rather than re-derived from the OSC 133 lines, because
/// the markers arrive ahead of the text and a range taken from them later would clip the tail.
/// </summary>
public partial class TerminalTabView
{
    private const int CapturedOutputLimit = 200;

    private readonly List<ShellCommandOutputEventArgs> _capturedOutputs = [];
    private ShellCommandOutputEventArgs? _contextMenuCommandOutput;

    /// <summary>
    /// Copies the output of the most recent finished command (OSC 133 shell integration) to the
    /// clipboard. Returns false when no command output has been captured yet.
    /// </summary>
    public bool CopyLastCommandOutput()
    {
        if (_capturedOutputs.Count == 0)
        {
            SetStatus("No command output to copy.");
            return false;
        }

        return CopyCommandOutput(_capturedOutputs[^1]);
    }

    private void RememberCapturedOutput(ShellCommandOutputEventArgs output)
    {
        _capturedOutputs.Add(output);
        if (_capturedOutputs.Count > CapturedOutputLimit)
        {
            _capturedOutputs.RemoveAt(0);
        }
    }

    private bool CopyCommandOutput(ShellCommandOutputEventArgs output)
    {
        try
        {
            Clipboard.SetText(output.Output);
        }
        catch (Exception ex)
        {
            SetStatus($"Clipboard update failed: {ex.Message}");
            return false;
        }

        SetStatus(DescribeCopiedOutput(output.CommandLine, output.Output));
        return true;
    }

    internal static string DescribeCopiedOutput(string? commandLine, string output)
    {
        int lineCount = output.Length == 0 ? 0 : output.Split(Environment.NewLine).Length;
        string what = string.IsNullOrWhiteSpace(commandLine) ? "command output" : $"output of '{commandLine}'";
        return $"Copied {what} ({lineCount} line{(lineCount == 1 ? "" : "s")}).";
    }

    private ShellCommandOutputEventArgs? FindCapturedOutputAtPoint(Point point) =>
        TerminalOutput.TryGetTextPositionFromPoint(point, out int line, out _)
            ? FindCapturedOutputForLine(DisplayToBufferLine(line))
            : null;

    /// <summary>
    /// The captured output of the command that owns <paramref name="absoluteLine"/> — its prompt,
    /// command line or output. The owner is found from the prompt marks, and its output is the
    /// capture whose start (C) lies between that prompt and the next one.
    /// </summary>
    internal ShellCommandOutputEventArgs? FindCapturedOutputForLine(int absoluteLine)
    {
        if (_commandNavigation.FindOwner(absoluteLine) is not { } owner)
        {
            return null;
        }

        int nextPrompt = int.MaxValue;
        foreach (int prompt in _commandNavigation.PromptLines)
        {
            if (prompt > owner.PromptLine && prompt < nextPrompt)
            {
                nextPrompt = prompt;
            }
        }

        for (int index = _capturedOutputs.Count - 1; index >= 0; index--)
        {
            ShellCommandOutputEventArgs output = _capturedOutputs[index];
            if (output.Start is { } start &&
                _terminalBuffer.TryResolveMark(start, out int startLine) &&
                startLine >= owner.PromptLine &&
                startLine < nextPrompt)
            {
                return output;
            }
        }

        return null;
    }

    /// <summary>The outputs kept for "Copy Command Output", oldest first; test seam.</summary>
    internal IReadOnlyList<ShellCommandOutputEventArgs> CapturedOutputsForTests => _capturedOutputs;

    private void CopyCommandOutputMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_contextMenuCommandOutput is { } output)
        {
            CopyCommandOutput(output);
        }

        QueueTerminalInputFocus();
    }
}
