using Terminal.Buffer;

namespace Terminal.Tabs;

internal sealed class TerminalCommandNavigationCoordinator
{
    private readonly List<int> _promptLines = [];

    public bool HasPrompts => _promptLines.Count > 0;
    public IReadOnlyList<int> PromptLines => _promptLines;

    public bool Observe(ShellCommandZoneType zoneType, int absoluteLine)
    {
        if (zoneType != ShellCommandZoneType.PromptStart ||
            _promptLines.Count > 0 && _promptLines[^1] == absoluteLine)
        {
            return false;
        }

        _promptLines.Add(absoluteLine);
        return true;
    }

    public void ResetSession()
    {
        _promptLines.Clear();
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
}
