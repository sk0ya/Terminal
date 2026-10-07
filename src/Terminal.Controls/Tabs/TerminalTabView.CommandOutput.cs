using System.Windows;

namespace Terminal.Tabs;

/// <summary>
/// "Copy Command Output": the ranges cut by <see cref="TerminalCommandOutputCoordinator"/> are kept
/// (bounded, as line marks only) so the latest one can be copied with a key and any earlier one from
/// the right-click menu over it; the text is read out at copy time. The cut ranges are reused rather
/// than re-derived from the OSC 133 lines, because the markers arrive ahead of the text and a range
/// taken from them later would clip the tail.
/// </summary>
public partial class TerminalTabView
{
    private const int CapturedOutputLimit = 200;

    private readonly List<CommandOutputRange> _capturedOutputs = [];
    private CommandOutputRange? _contextMenuCommandOutput;

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

    private void RememberCapturedOutput(CommandOutputRange output)
    {
        _capturedOutputs.Add(output);
        if (_capturedOutputs.Count > CapturedOutputLimit)
        {
            _capturedOutputs.RemoveAt(0);
        }
    }

    private bool CopyCommandOutput(CommandOutputRange range)
    {
        if (TerminalCommandOutputCoordinator.Extract(_terminalBuffer, range) is not { } output)
        {
            SetStatus("That command's output is no longer in the buffer.");
            return false;
        }

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

    private CommandOutputRange? FindCapturedOutputAtPoint(Point point) =>
        TerminalOutput.TryGetTextPositionFromPoint(point, out int line, out _)
            ? FindCapturedOutputForLine(DisplayToBufferLine(line))
            : null;

    /// <summary>
    /// The captured output of the command that owns <paramref name="absoluteLine"/> — its prompt,
    /// command line or output. The owner is found from the prompt marks, and its output is the
    /// capture whose start (C) lies between that prompt and the next one.
    /// </summary>
    internal CommandOutputRange? FindCapturedOutputForLine(int absoluteLine)
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
            CommandOutputRange output = _capturedOutputs[index];
            if (_terminalBuffer.TryResolveMark(output.Start, out int startLine) &&
                startLine >= owner.PromptLine &&
                startLine < nextPrompt)
            {
                return output;
            }
        }

        return null;
    }

    /// <summary>The ranges kept for "Copy Command Output", oldest first; test seam.</summary>
    internal IReadOnlyList<CommandOutputRange> CapturedOutputsForTests => _capturedOutputs;

    /// <summary>The text of a kept range, as a copy would read it; test seam.</summary>
    internal string? ReadCapturedOutputForTests(CommandOutputRange? range) =>
        range is null ? null : TerminalCommandOutputCoordinator.Extract(_terminalBuffer, range)?.Output;

    private void CopyCommandOutputMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_contextMenuCommandOutput is { } output)
        {
            CopyCommandOutput(output);
        }

        QueueTerminalInputFocus();
    }
}
