using Terminal.Buffer;

namespace Terminal.Tabs;

/// <summary>
/// One shell command observed through OSC 133: where its prompt started (A), the line the
/// command was typed on (B), and whether it actually ran (C). Used by sticky scroll to show
/// which command produced the output at the top of the viewport.
/// </summary>
internal readonly record struct TerminalCommandMark(int PromptLine, int CommandLine, bool Executed);

internal sealed class TerminalCommandNavigationCoordinator
{
    private readonly List<int> _promptLines = [];
    private readonly List<TerminalCommandMark> _commands = [];

    public bool HasPrompts => _promptLines.Count > 0;
    public IReadOnlyList<int> PromptLines => _promptLines;
    public IReadOnlyList<TerminalCommandMark> Commands => _commands;

    public bool Observe(ShellCommandZoneType zoneType, int absoluteLine)
    {
        switch (zoneType)
        {
            case ShellCommandZoneType.CommandStart:
                // B lands on the last prompt line, so a multi-line prompt still sticks the line
                // that actually carries the command.
                UpdateLastCommand(mark => absoluteLine >= mark.PromptLine
                    ? mark with { CommandLine = absoluteLine }
                    : mark);
                return false;
            case ShellCommandZoneType.CommandExecuted:
                UpdateLastCommand(mark => mark with { Executed = true });
                return false;
            case ShellCommandZoneType.PromptStart:
                break;
            default:
                return false;
        }

        if (_promptLines.Count > 0 && _promptLines[^1] == absoluteLine)
        {
            return false;
        }

        _promptLines.Add(absoluteLine);
        _commands.Add(new TerminalCommandMark(absoluteLine, absoluteLine, Executed: false));
        return true;
    }

    public void ResetSession()
    {
        _promptLines.Clear();
        _commands.Clear();
    }

    public int? FindAdjacent(int currentTopLine, bool upward)
    {
        if (upward)
        {
            for (int index = _promptLines.Count - 1; index >= 0; index--)
            {
                if (_promptLines[index] < currentTopLine)
                {
                    return _promptLines[index];
                }
            }

            return null;
        }

        foreach (int line in _promptLines)
        {
            if (line > currentTopLine)
            {
                return line;
            }
        }

        return null;
    }

    /// <summary>
    /// The command line to pin above the viewport when <paramref name="topLine"/> is the first
    /// visible line, or null when nothing should stick. The owner of the top line is the command
    /// whose prompt started last at or above it (ties go to the most recent one, since a cleared
    /// screen can reuse line numbers). It sticks only once it has run and its own command line
    /// has scrolled out above the top — while the command line is still visible there is
    /// nothing to recall, and an un-run prompt has no output to label.
    /// </summary>
    public int? FindStickyCommandLine(int topLine)
    {
        int ownerIndex = -1;
        for (int index = 0; index < _commands.Count; index++)
        {
            int prompt = _commands[index].PromptLine;
            if (prompt <= topLine && (ownerIndex < 0 || prompt >= _commands[ownerIndex].PromptLine))
            {
                ownerIndex = index;
            }
        }

        if (ownerIndex < 0)
        {
            return null;
        }

        TerminalCommandMark owner = _commands[ownerIndex];
        return owner.Executed && owner.CommandLine < topLine ? owner.CommandLine : null;
    }

    private void UpdateLastCommand(Func<TerminalCommandMark, TerminalCommandMark> update)
    {
        if (_commands.Count > 0)
        {
            _commands[^1] = update(_commands[^1]);
        }
    }
}
