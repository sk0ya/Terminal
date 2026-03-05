using System.Text;

namespace ConPtyTerminal;

internal sealed class AnsiTerminalBuffer
{
    private enum ParserState
    {
        Normal,
        Escape,
        Csi,
        Osc,
        OscEscape
    }

    private int _columns;
    private int _rows;
    private char[][] _cells;
    private int _cursorRow;
    private int _cursorColumn;
    private int _savedCursorRow;
    private int _savedCursorColumn;
    private ParserState _state;
    private readonly StringBuilder _csiBuffer = new();

    public AnsiTerminalBuffer(short columns, short rows)
    {
        _columns = Math.Max(columns, (short)20);
        _rows = Math.Max(rows, (short)10);
        _cells = CreateCells(_columns, _rows);
    }

    public void Resize(short columns, short rows)
    {
        int newColumns = Math.Max(columns, (short)20);
        int newRows = Math.Max(rows, (short)10);

        if (newColumns == _columns && newRows == _rows)
        {
            return;
        }

        char[][] resized = CreateCells(newColumns, newRows);
        int copyRows = Math.Min(_rows, newRows);
        int copyColumns = Math.Min(_columns, newColumns);

        for (int row = 0; row < copyRows; row++)
        {
            Array.Copy(_cells[row], 0, resized[row], 0, copyColumns);
        }

        _cells = resized;
        _columns = newColumns;
        _rows = newRows;
        _cursorRow = Math.Clamp(_cursorRow, 0, _rows - 1);
        _cursorColumn = Math.Clamp(_cursorColumn, 0, _columns - 1);
    }

    public void Process(string text)
    {
        foreach (char ch in text)
        {
            ProcessChar(ch);
        }
    }

    public string GetDisplayText()
    {
        int lastNonEmptyRow = _rows - 1;
        while (lastNonEmptyRow > 0 && IsRowEmpty(lastNonEmptyRow))
        {
            lastNonEmptyRow--;
        }

        var builder = new StringBuilder((lastNonEmptyRow + 1) * (_columns + 2));
        for (int row = 0; row <= lastNonEmptyRow; row++)
        {
            int lastNonBlankColumn = _columns - 1;
            while (lastNonBlankColumn >= 0 && _cells[row][lastNonBlankColumn] == ' ')
            {
                lastNonBlankColumn--;
            }

            if (lastNonBlankColumn >= 0)
            {
                builder.Append(_cells[row], 0, lastNonBlankColumn + 1);
            }

            if (row < lastNonEmptyRow)
            {
                builder.AppendLine();
            }
        }

        return builder.ToString();
    }

    private static char[][] CreateCells(int columns, int rows)
    {
        char[][] cells = new char[rows][];
        for (int row = 0; row < rows; row++)
        {
            cells[row] = new string(' ', columns).ToCharArray();
        }

        return cells;
    }

    private void ProcessChar(char ch)
    {
        switch (_state)
        {
            case ParserState.Normal:
                ProcessNormal(ch);
                break;
            case ParserState.Escape:
                ProcessEscape(ch);
                break;
            case ParserState.Csi:
                ProcessCsi(ch);
                break;
            case ParserState.Osc:
                ProcessOsc(ch);
                break;
            case ParserState.OscEscape:
                ProcessOscEscape(ch);
                break;
        }
    }

    private void ProcessNormal(char ch)
    {
        switch (ch)
        {
            case '\u001b':
                _state = ParserState.Escape;
                return;
            case '\r':
                _cursorColumn = 0;
                return;
            case '\n':
                MoveDownAndScrollIfNeeded();
                return;
            case '\b':
                _cursorColumn = Math.Max(0, _cursorColumn - 1);
                return;
            case '\t':
                int nextTab = ((_cursorColumn / 8) + 1) * 8;
                while (_cursorColumn < nextTab && _cursorColumn < _columns)
                {
                    PutChar(' ');
                }

                return;
            default:
                if (ch >= ' ' && ch != '\u007f')
                {
                    PutChar(ch);
                }

                return;
        }
    }

    private void ProcessEscape(char ch)
    {
        switch (ch)
        {
            case '[':
                _csiBuffer.Clear();
                _state = ParserState.Csi;
                return;
            case ']':
                _state = ParserState.Osc;
                return;
            case '7':
                _savedCursorRow = _cursorRow;
                _savedCursorColumn = _cursorColumn;
                _state = ParserState.Normal;
                return;
            case '8':
                _cursorRow = Math.Clamp(_savedCursorRow, 0, _rows - 1);
                _cursorColumn = Math.Clamp(_savedCursorColumn, 0, _columns - 1);
                _state = ParserState.Normal;
                return;
            case 'c':
                ClearDisplay(2);
                _cursorRow = 0;
                _cursorColumn = 0;
                _state = ParserState.Normal;
                return;
            case 'D':
                MoveDownAndScrollIfNeeded();
                _state = ParserState.Normal;
                return;
            case 'E':
                MoveDownAndScrollIfNeeded();
                _cursorColumn = 0;
                _state = ParserState.Normal;
                return;
            case 'M':
                _cursorRow = Math.Max(0, _cursorRow - 1);
                _state = ParserState.Normal;
                return;
            default:
                _state = ParserState.Normal;
                return;
        }
    }

    private void ProcessCsi(char ch)
    {
        if (ch >= '@' && ch <= '~')
        {
            DispatchCsi(ch, _csiBuffer.ToString());
            _state = ParserState.Normal;
            return;
        }

        _csiBuffer.Append(ch);
    }

    private void ProcessOsc(char ch)
    {
        if (ch == '\a')
        {
            _state = ParserState.Normal;
            return;
        }

        if (ch == '\u009c')
        {
            _state = ParserState.Normal;
            return;
        }

        if (ch == '\u001b')
        {
            _state = ParserState.OscEscape;
            return;
        }
    }

    private void ProcessOscEscape(char ch)
    {
        if (ch == '\\')
        {
            _state = ParserState.Normal;
            return;
        }

        // Some streams end OSC with a bare ESC and immediately start another escape sequence.
        _state = ParserState.Escape;
        ProcessEscape(ch);
    }

    private void DispatchCsi(char command, string rawParams)
    {
        bool isPrivate = rawParams.StartsWith("?", StringComparison.Ordinal);
        string paramText = isPrivate ? rawParams[1..] : rawParams;
        int?[] parameters = ParseParameters(paramText);

        switch (command)
        {
            case 'A':
                _cursorRow = Math.Max(0, _cursorRow - GetParameter(parameters, 0, 1));
                break;
            case 'B':
                _cursorRow = Math.Min(_rows - 1, _cursorRow + GetParameter(parameters, 0, 1));
                break;
            case 'C':
                _cursorColumn = Math.Min(_columns - 1, _cursorColumn + GetParameter(parameters, 0, 1));
                break;
            case 'D':
                _cursorColumn = Math.Max(0, _cursorColumn - GetParameter(parameters, 0, 1));
                break;
            case 'E':
                _cursorRow = Math.Min(_rows - 1, _cursorRow + GetParameter(parameters, 0, 1));
                _cursorColumn = 0;
                break;
            case 'F':
                _cursorRow = Math.Max(0, _cursorRow - GetParameter(parameters, 0, 1));
                _cursorColumn = 0;
                break;
            case 'G':
                _cursorColumn = Math.Clamp(GetParameter(parameters, 0, 1) - 1, 0, _columns - 1);
                break;
            case 'H':
            case 'f':
                _cursorRow = Math.Clamp(GetParameter(parameters, 0, 1) - 1, 0, _rows - 1);
                _cursorColumn = Math.Clamp(GetParameter(parameters, 1, 1) - 1, 0, _columns - 1);
                break;
            case 'J':
                ClearDisplay(GetParameter(parameters, 0, 0));
                break;
            case 'K':
                ClearLine(GetParameter(parameters, 0, 0));
                break;
            case 'm':
                break;
            case 's':
                _savedCursorRow = _cursorRow;
                _savedCursorColumn = _cursorColumn;
                break;
            case 'u':
                _cursorRow = Math.Clamp(_savedCursorRow, 0, _rows - 1);
                _cursorColumn = Math.Clamp(_savedCursorColumn, 0, _columns - 1);
                break;
            case 'h':
            case 'l':
                if (!isPrivate)
                {
                    break;
                }

                break;
            default:
                break;
        }
    }

    private static int?[] ParseParameters(string paramText)
    {
        if (string.IsNullOrEmpty(paramText))
        {
            return Array.Empty<int?>();
        }

        string[] parts = paramText.Split(';');
        var result = new int?[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (int.TryParse(parts[i], out int value))
            {
                result[i] = value;
            }
            else
            {
                result[i] = null;
            }
        }

        return result;
    }

    private static int GetParameter(int?[] parameters, int index, int defaultValue)
    {
        if (index >= parameters.Length)
        {
            return defaultValue;
        }

        int? value = parameters[index];
        if (!value.HasValue || value.Value == 0)
        {
            return defaultValue;
        }

        return value.Value;
    }

    private void ClearDisplay(int mode)
    {
        switch (mode)
        {
            case 0:
                for (int row = _cursorRow; row < _rows; row++)
                {
                    int start = row == _cursorRow ? _cursorColumn : 0;
                    Array.Fill(_cells[row], ' ', start, _columns - start);
                }

                break;
            case 1:
                for (int row = 0; row <= _cursorRow; row++)
                {
                    int end = row == _cursorRow ? _cursorColumn : _columns - 1;
                    Array.Fill(_cells[row], ' ', 0, end + 1);
                }

                break;
            case 2:
            case 3:
                for (int row = 0; row < _rows; row++)
                {
                    Array.Fill(_cells[row], ' ');
                }

                break;
            default:
                break;
        }
    }

    private void ClearLine(int mode)
    {
        switch (mode)
        {
            case 0:
                Array.Fill(_cells[_cursorRow], ' ', _cursorColumn, _columns - _cursorColumn);
                break;
            case 1:
                Array.Fill(_cells[_cursorRow], ' ', 0, _cursorColumn + 1);
                break;
            case 2:
                Array.Fill(_cells[_cursorRow], ' ');
                break;
            default:
                break;
        }
    }

    private void PutChar(char ch)
    {
        _cells[_cursorRow][_cursorColumn] = ch;
        _cursorColumn++;

        if (_cursorColumn >= _columns)
        {
            _cursorColumn = 0;
            MoveDownAndScrollIfNeeded();
        }
    }

    private void MoveDownAndScrollIfNeeded()
    {
        _cursorRow++;
        if (_cursorRow < _rows)
        {
            return;
        }

        ScrollUp(1);
        _cursorRow = _rows - 1;
    }

    private void ScrollUp(int lines)
    {
        int count = Math.Clamp(lines, 1, _rows);
        for (int row = 0; row < _rows - count; row++)
        {
            _cells[row] = _cells[row + count];
        }

        for (int row = _rows - count; row < _rows; row++)
        {
            _cells[row] = new string(' ', _columns).ToCharArray();
        }
    }

    private bool IsRowEmpty(int row)
    {
        for (int col = 0; col < _columns; col++)
        {
            if (_cells[row][col] != ' ')
            {
                return false;
            }
        }

        return true;
    }
}
