using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace ConPtyTerminal;

internal sealed class AnsiTerminalBuffer
{
    private const int MinColumns = 20;
    private const int MinRows = 10;
    private const int DefaultScrollbackLimit = 2000;

    private static readonly Color DefaultForeground = Color.FromRgb(0xE3, 0xE3, 0xE3);
    private static readonly Color DefaultBackground = Color.FromRgb(0x11, 0x11, 0x11);
    private static readonly Color CursorAccent = Color.FromRgb(0x5F, 0xAF, 0xFF);
    private static readonly Color[] AnsiPalette =
    {
        Color.FromRgb(0x1C, 0x1C, 0x1C),
        Color.FromRgb(0xC5, 0x0F, 0x1F),
        Color.FromRgb(0x13, 0xA1, 0x0E),
        Color.FromRgb(0xC1, 0x9C, 0x00),
        Color.FromRgb(0x00, 0x37, 0xDA),
        Color.FromRgb(0x88, 0x17, 0x98),
        Color.FromRgb(0x3A, 0x96, 0xDD),
        Color.FromRgb(0xCC, 0xCC, 0xCC),
        Color.FromRgb(0x76, 0x76, 0x76),
        Color.FromRgb(0xE7, 0x48, 0x56),
        Color.FromRgb(0x16, 0xC6, 0x0C),
        Color.FromRgb(0xF9, 0xF1, 0xA5),
        Color.FromRgb(0x3B, 0x78, 0xFF),
        Color.FromRgb(0xB4, 0x00, 0x9E),
        Color.FromRgb(0x61, 0xD6, 0xD6),
        Color.FromRgb(0xF2, 0xF2, 0xF2)
    };
    private static readonly Dictionary<Color, SolidColorBrush> BrushCache = [];

    private readonly int _scrollbackLimit;
    private readonly List<TerminalLine> _scrollback = [];
    private readonly StringBuilder _csiBuffer = new();
    private readonly StringBuilder _oscBuffer = new();

    private List<TerminalLine> _screen;
    private ScreenState? _primaryScreenBackup;
    private int _columns;
    private int _rows;
    private int _cursorRow;
    private int _cursorColumn;
    private int _savedCursorRow;
    private int _savedCursorColumn;
    private int _scrollTop;
    private int _scrollBottom;
    private ParserState _state;
    private TerminalStyle _currentStyle = TerminalStyle.Default;
    private TerminalStyle _savedStyle = TerminalStyle.Default;
    private bool _cursorVisible = true;
    private string _windowTitle = string.Empty;

    public AnsiTerminalBuffer(short columns, short rows, int scrollbackLimit = DefaultScrollbackLimit)
    {
        _scrollbackLimit = Math.Max(scrollbackLimit, rows);
        _columns = Math.Max(columns, (short)MinColumns);
        _rows = Math.Max(rows, (short)MinRows);
        _screen = CreateScreen(_rows, _columns, TerminalStyle.Default);
        ResetMargins();
    }

    public string WindowTitle => _windowTitle;

    public void Resize(short columns, short rows)
    {
        int newColumns = Math.Max(columns, (short)MinColumns);
        int newRows = Math.Max(rows, (short)MinRows);

        if (newColumns == _columns && newRows == _rows)
        {
            return;
        }

        var resizedScreen = CreateScreen(newRows, newColumns, TerminalStyle.Default);
        int sourceStartRow = Math.Max(0, _rows - newRows);
        int targetStartRow = Math.Max(0, newRows - _rows);
        int copyRows = Math.Min(_rows, newRows);
        int copyColumns = Math.Min(_columns, newColumns);

        if (sourceStartRow > 0)
        {
            for (int row = 0; row < sourceStartRow; row++)
            {
                AppendScrollback(CloneLine(_screen[row]));
            }
        }

        for (int row = 0; row < copyRows; row++)
        {
            TerminalLine source = _screen[sourceStartRow + row];
            TerminalLine target = resizedScreen[targetStartRow + row];
            Array.Copy(source.Cells, 0, target.Cells, 0, copyColumns);
        }

        _screen = resizedScreen;
        _columns = newColumns;
        _rows = newRows;
        _cursorRow = Math.Clamp(_cursorRow - sourceStartRow + targetStartRow, 0, _rows - 1);
        _cursorColumn = Math.Clamp(_cursorColumn, 0, _columns - 1);
        _savedCursorRow = Math.Clamp(_savedCursorRow, 0, _rows - 1);
        _savedCursorColumn = Math.Clamp(_savedCursorColumn, 0, _columns - 1);
        ResetMargins();
    }

    public void Process(string text)
    {
        foreach (char ch in text)
        {
            ProcessChar(ch);
        }
    }

    public FlowDocument CreateDocument(FontFamily fontFamily, double fontSize, bool showCursor)
    {
        var document = new FlowDocument
        {
            FontFamily = fontFamily,
            FontSize = fontSize,
            PagePadding = new Thickness(0),
            Background = GetBrush(DefaultBackground),
            TextAlignment = TextAlignment.Left
        };

        var paragraph = new Paragraph
        {
            Margin = new Thickness(0)
        };

        bool isFirstLine = true;
        foreach (TerminalLine line in _scrollback)
        {
            AppendLine(paragraph.Inlines, line, -1, showCursor: false, ref isFirstLine);
        }

        int lastScreenRow = FindLastVisibleScreenRow(showCursor);
        for (int row = 0; row <= lastScreenRow; row++)
        {
            int cursorColumn = showCursor && _cursorVisible && row == _cursorRow ? _cursorColumn : -1;
            AppendLine(paragraph.Inlines, _screen[row], cursorColumn, showCursor, ref isFirstLine);
        }

        if (paragraph.Inlines.Count == 0)
        {
            paragraph.Inlines.Add(new Run(string.Empty));
        }

        document.Blocks.Add(paragraph);
        return document;
    }

    public void ClearScrollback()
    {
        _scrollback.Clear();
    }

    private static List<TerminalLine> CreateScreen(int rows, int columns, TerminalStyle blankStyle)
    {
        var screen = new List<TerminalLine>(rows);
        for (int row = 0; row < rows; row++)
        {
            screen.Add(new TerminalLine(columns, blankStyle));
        }

        return screen;
    }

    private static TerminalLine CloneLine(TerminalLine line)
    {
        var clone = new TerminalLine(line.Cells.Length, TerminalStyle.Default);
        Array.Copy(line.Cells, clone.Cells, line.Cells.Length);
        return clone;
    }

    private static SolidColorBrush GetBrush(Color color)
    {
        if (BrushCache.TryGetValue(color, out SolidColorBrush? existing))
        {
            return existing;
        }

        var brush = new SolidColorBrush(color);
        brush.Freeze();
        BrushCache[color] = brush;
        return brush;
    }

    private void AppendScrollback(TerminalLine line)
    {
        _scrollback.Add(line);
        int overflow = _scrollback.Count - _scrollbackLimit;
        if (overflow > 0)
        {
            _scrollback.RemoveRange(0, overflow);
        }
    }

    private void ResetMargins()
    {
        _scrollTop = 0;
        _scrollBottom = _rows - 1;
    }

    private void ResetTerminal()
    {
        _scrollback.Clear();
        _screen = CreateScreen(_rows, _columns, TerminalStyle.Default);
        _primaryScreenBackup = null;
        _cursorRow = 0;
        _cursorColumn = 0;
        _savedCursorRow = 0;
        _savedCursorColumn = 0;
        _currentStyle = TerminalStyle.Default;
        _savedStyle = TerminalStyle.Default;
        _cursorVisible = true;
        _windowTitle = string.Empty;
        _state = ParserState.Normal;
        _csiBuffer.Clear();
        _oscBuffer.Clear();
        ResetMargins();
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
            case '\u0007':
                return;
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
                if (!char.IsControl(ch))
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
                _oscBuffer.Clear();
                _state = ParserState.Osc;
                return;
            case '7':
                SaveCursorState();
                _state = ParserState.Normal;
                return;
            case '8':
                RestoreCursorState();
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
                ReverseIndex();
                _state = ParserState.Normal;
                return;
            case 'c':
                ResetTerminal();
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
            DispatchOsc(_oscBuffer.ToString());
            _state = ParserState.Normal;
            return;
        }

        if (ch == '\u009c')
        {
            DispatchOsc(_oscBuffer.ToString());
            _state = ParserState.Normal;
            return;
        }

        if (ch == '\u001b')
        {
            _state = ParserState.OscEscape;
            return;
        }

        _oscBuffer.Append(ch);
    }

    private void ProcessOscEscape(char ch)
    {
        if (ch == '\\')
        {
            DispatchOsc(_oscBuffer.ToString());
            _state = ParserState.Normal;
            return;
        }

        _state = ParserState.Escape;
        ProcessEscape(ch);
    }

    private void DispatchOsc(string content)
    {
        int separatorIndex = content.IndexOf(';');
        if (separatorIndex <= 0)
        {
            return;
        }

        string command = content[..separatorIndex];
        string value = content[(separatorIndex + 1)..];
        if (command is "0" or "2")
        {
            _windowTitle = value;
        }
    }

    private void DispatchCsi(char command, string rawParams)
    {
        bool isPrivate = rawParams.StartsWith("?", StringComparison.Ordinal);
        string paramText = isPrivate ? rawParams[1..] : rawParams;
        int?[] parameters = ParseParameters(paramText);

        switch (command)
        {
            case '@':
                InsertCharacters(GetParameter(parameters, 0, 1));
                break;
            case 'A':
                _cursorRow = Math.Max(_scrollTop, _cursorRow - GetParameter(parameters, 0, 1));
                break;
            case 'B':
                _cursorRow = Math.Min(_scrollBottom, _cursorRow + GetParameter(parameters, 0, 1));
                break;
            case 'C':
                _cursorColumn = Math.Min(_columns - 1, _cursorColumn + GetParameter(parameters, 0, 1));
                break;
            case 'D':
                _cursorColumn = Math.Max(0, _cursorColumn - GetParameter(parameters, 0, 1));
                break;
            case 'E':
                _cursorRow = Math.Min(_scrollBottom, _cursorRow + GetParameter(parameters, 0, 1));
                _cursorColumn = 0;
                break;
            case 'F':
                _cursorRow = Math.Max(_scrollTop, _cursorRow - GetParameter(parameters, 0, 1));
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
            case 'L':
                InsertLines(GetParameter(parameters, 0, 1));
                break;
            case 'M':
                DeleteLines(GetParameter(parameters, 0, 1));
                break;
            case 'P':
                DeleteCharacters(GetParameter(parameters, 0, 1));
                break;
            case 'S':
                ScrollUpRegion(GetParameter(parameters, 0, 1), _scrollTop, _scrollBottom);
                break;
            case 'T':
                ScrollDownRegion(GetParameter(parameters, 0, 1), _scrollTop, _scrollBottom);
                break;
            case 'X':
                EraseCharacters(GetParameter(parameters, 0, 1));
                break;
            case 'd':
                _cursorRow = Math.Clamp(GetParameter(parameters, 0, 1) - 1, 0, _rows - 1);
                break;
            case 'h':
            case 'l':
                if (isPrivate)
                {
                    SetPrivateMode(parameters, command == 'h');
                }

                break;
            case 'm':
                ApplySgr(parameters);
                break;
            case 'r':
                SetScrollRegion(parameters);
                break;
            case 's':
                SaveCursorState();
                break;
            case 'u':
                RestoreCursorState();
                break;
        }
    }

    private void SaveCursorState()
    {
        _savedCursorRow = _cursorRow;
        _savedCursorColumn = _cursorColumn;
        _savedStyle = _currentStyle;
    }

    private void RestoreCursorState()
    {
        _cursorRow = Math.Clamp(_savedCursorRow, 0, _rows - 1);
        _cursorColumn = Math.Clamp(_savedCursorColumn, 0, _columns - 1);
        _currentStyle = _savedStyle;
    }

    private void ReverseIndex()
    {
        if (_cursorRow == _scrollTop)
        {
            ScrollDownRegion(1, _scrollTop, _scrollBottom);
            return;
        }

        _cursorRow = Math.Max(0, _cursorRow - 1);
    }

    private void SetPrivateMode(int?[] parameters, bool enabled)
    {
        foreach (int? parameter in parameters)
        {
            switch (parameter)
            {
                case 25:
                    _cursorVisible = enabled;
                    break;
                case 47:
                case 1047:
                case 1049:
                    if (enabled)
                    {
                        EnterAlternateScreen();
                    }
                    else
                    {
                        ExitAlternateScreen();
                    }

                    break;
            }
        }
    }

    private void EnterAlternateScreen()
    {
        if (_primaryScreenBackup is not null)
        {
            return;
        }

        _primaryScreenBackup = new ScreenState(
            CloneScreen(_screen),
            _cursorRow,
            _cursorColumn,
            _savedCursorRow,
            _savedCursorColumn,
            _scrollTop,
            _scrollBottom,
            _currentStyle,
            _savedStyle);

        _screen = CreateScreen(_rows, _columns, TerminalStyle.Default);
        _cursorRow = 0;
        _cursorColumn = 0;
        _savedCursorRow = 0;
        _savedCursorColumn = 0;
        _currentStyle = TerminalStyle.Default;
        _savedStyle = TerminalStyle.Default;
        ResetMargins();
    }

    private void ExitAlternateScreen()
    {
        if (_primaryScreenBackup is null)
        {
            return;
        }

        _screen = CloneScreen(_primaryScreenBackup.Screen);
        _cursorRow = _primaryScreenBackup.CursorRow;
        _cursorColumn = _primaryScreenBackup.CursorColumn;
        _savedCursorRow = _primaryScreenBackup.SavedCursorRow;
        _savedCursorColumn = _primaryScreenBackup.SavedCursorColumn;
        _scrollTop = _primaryScreenBackup.ScrollTop;
        _scrollBottom = _primaryScreenBackup.ScrollBottom;
        _currentStyle = _primaryScreenBackup.Style;
        _savedStyle = _primaryScreenBackup.SavedStyle;
        _primaryScreenBackup = null;
    }

    private static List<TerminalLine> CloneScreen(List<TerminalLine> source)
    {
        var clone = new List<TerminalLine>(source.Count);
        foreach (TerminalLine line in source)
        {
            clone.Add(CloneLine(line));
        }

        return clone;
    }

    private void ApplySgr(int?[] parameters)
    {
        if (parameters.Length == 0)
        {
            _currentStyle = TerminalStyle.Default;
            return;
        }

        for (int index = 0; index < parameters.Length; index++)
        {
            int code = parameters[index] ?? 0;
            switch (code)
            {
                case 0:
                    _currentStyle = TerminalStyle.Default;
                    break;
                case 1:
                    _currentStyle = _currentStyle with { Bold = true };
                    break;
                case 4:
                    _currentStyle = _currentStyle with { Underline = true };
                    break;
                case 22:
                    _currentStyle = _currentStyle with { Bold = false };
                    break;
                case 24:
                    _currentStyle = _currentStyle with { Underline = false };
                    break;
                case 7:
                    _currentStyle = _currentStyle with { Inverse = true };
                    break;
                case 27:
                    _currentStyle = _currentStyle with { Inverse = false };
                    break;
                case >= 30 and <= 37:
                    _currentStyle = _currentStyle with { Foreground = AnsiPalette[code - 30] };
                    break;
                case 39:
                    _currentStyle = _currentStyle with { Foreground = null };
                    break;
                case >= 40 and <= 47:
                    _currentStyle = _currentStyle with { Background = AnsiPalette[code - 40] };
                    break;
                case 49:
                    _currentStyle = _currentStyle with { Background = null };
                    break;
                case >= 90 and <= 97:
                    _currentStyle = _currentStyle with { Foreground = AnsiPalette[8 + (code - 90)] };
                    break;
                case >= 100 and <= 107:
                    _currentStyle = _currentStyle with { Background = AnsiPalette[8 + (code - 100)] };
                    break;
                case 38:
                case 48:
                    if (TryReadExtendedColor(parameters, ref index, out Color color))
                    {
                        if (code == 38)
                        {
                            _currentStyle = _currentStyle with { Foreground = color };
                        }
                        else
                        {
                            _currentStyle = _currentStyle with { Background = color };
                        }
                    }

                    break;
            }
        }
    }

    private static bool TryReadExtendedColor(int?[] parameters, ref int index, out Color color)
    {
        color = default;
        if (index + 1 >= parameters.Length || !parameters[index + 1].HasValue)
        {
            return false;
        }

        int mode = parameters[index + 1]!.Value;
        if (mode == 5 && index + 2 < parameters.Length && parameters[index + 2].HasValue)
        {
            color = ResolveXtermColor(parameters[index + 2]!.Value);
            index += 2;
            return true;
        }

        if (mode == 2 &&
            index + 4 < parameters.Length &&
            parameters[index + 2].HasValue &&
            parameters[index + 3].HasValue &&
            parameters[index + 4].HasValue)
        {
            color = Color.FromRgb(
                (byte)Math.Clamp(parameters[index + 2]!.Value, 0, 255),
                (byte)Math.Clamp(parameters[index + 3]!.Value, 0, 255),
                (byte)Math.Clamp(parameters[index + 4]!.Value, 0, 255));
            index += 4;
            return true;
        }

        return false;
    }

    private static Color ResolveXtermColor(int index)
    {
        if (index < 0)
        {
            return DefaultForeground;
        }

        if (index < AnsiPalette.Length)
        {
            return AnsiPalette[index];
        }

        if (index <= 231)
        {
            int value = index - 16;
            int red = value / 36;
            int green = (value / 6) % 6;
            int blue = value % 6;
            return Color.FromRgb(
                ScaleCubeComponent(red),
                ScaleCubeComponent(green),
                ScaleCubeComponent(blue));
        }

        if (index <= 255)
        {
            byte shade = (byte)(8 + ((index - 232) * 10));
            return Color.FromRgb(shade, shade, shade);
        }

        return DefaultForeground;
    }

    private static byte ScaleCubeComponent(int value)
    {
        return value == 0 ? (byte)0 : (byte)(55 + (value * 40));
    }

    private void SetScrollRegion(int?[] parameters)
    {
        int top = Math.Clamp(GetParameter(parameters, 0, 1) - 1, 0, _rows - 1);
        int bottom = Math.Clamp(GetParameter(parameters, 1, _rows) - 1, 0, _rows - 1);
        if (bottom <= top)
        {
            ResetMargins();
        }
        else
        {
            _scrollTop = top;
            _scrollBottom = bottom;
        }

        _cursorRow = 0;
        _cursorColumn = 0;
    }

    private void InsertLines(int count)
    {
        if (_cursorRow < _scrollTop || _cursorRow > _scrollBottom)
        {
            return;
        }

        int lineCount = Math.Min(Math.Max(count, 1), _scrollBottom - _cursorRow + 1);
        for (int row = _scrollBottom; row >= _cursorRow + lineCount; row--)
        {
            _screen[row] = _screen[row - lineCount];
        }

        for (int row = 0; row < lineCount; row++)
        {
            _screen[_cursorRow + row] = new TerminalLine(_columns, _currentStyle);
        }
    }

    private void DeleteLines(int count)
    {
        if (_cursorRow < _scrollTop || _cursorRow > _scrollBottom)
        {
            return;
        }

        int lineCount = Math.Min(Math.Max(count, 1), _scrollBottom - _cursorRow + 1);
        for (int row = _cursorRow; row <= _scrollBottom - lineCount; row++)
        {
            _screen[row] = _screen[row + lineCount];
        }

        for (int row = _scrollBottom - lineCount + 1; row <= _scrollBottom; row++)
        {
            _screen[row] = new TerminalLine(_columns, _currentStyle);
        }
    }

    private void InsertCharacters(int count)
    {
        int insertCount = Math.Min(Math.Max(count, 1), _columns - _cursorColumn);
        TerminalCell[] cells = _screen[_cursorRow].Cells;
        for (int column = _columns - 1; column >= _cursorColumn + insertCount; column--)
        {
            cells[column] = cells[column - insertCount];
        }

        for (int column = _cursorColumn; column < _cursorColumn + insertCount; column++)
        {
            cells[column] = CreateBlankCell(_currentStyle);
        }
    }

    private void DeleteCharacters(int count)
    {
        int deleteCount = Math.Min(Math.Max(count, 1), _columns - _cursorColumn);
        TerminalCell[] cells = _screen[_cursorRow].Cells;
        for (int column = _cursorColumn; column < _columns - deleteCount; column++)
        {
            cells[column] = cells[column + deleteCount];
        }

        for (int column = _columns - deleteCount; column < _columns; column++)
        {
            cells[column] = CreateBlankCell(_currentStyle);
        }
    }

    private void EraseCharacters(int count)
    {
        int eraseCount = Math.Min(Math.Max(count, 1), _columns - _cursorColumn);
        TerminalCell[] cells = _screen[_cursorRow].Cells;
        for (int column = _cursorColumn; column < _cursorColumn + eraseCount; column++)
        {
            cells[column] = CreateBlankCell(_currentStyle);
        }
    }

    private void ClearDisplay(int mode)
    {
        switch (mode)
        {
            case 0:
                ClearLine(0);
                for (int row = _cursorRow + 1; row < _rows; row++)
                {
                    ClearEntireLine(row);
                }

                break;
            case 1:
                for (int row = 0; row < _cursorRow; row++)
                {
                    ClearEntireLine(row);
                }

                ClearLine(1);
                break;
            case 2:
                for (int row = 0; row < _rows; row++)
                {
                    ClearEntireLine(row);
                }

                break;
            case 3:
                _scrollback.Clear();
                for (int row = 0; row < _rows; row++)
                {
                    ClearEntireLine(row);
                }

                break;
        }
    }

    private void ClearLine(int mode)
    {
        switch (mode)
        {
            case 0:
                FillRange(_screen[_cursorRow], _cursorColumn, _columns);
                break;
            case 1:
                FillRange(_screen[_cursorRow], 0, _cursorColumn + 1);
                break;
            case 2:
                ClearEntireLine(_cursorRow);
                break;
        }
    }

    private void ClearEntireLine(int row)
    {
        FillRange(_screen[row], 0, _columns);
    }

    private void FillRange(TerminalLine line, int startColumn, int endExclusive)
    {
        int start = Math.Clamp(startColumn, 0, _columns);
        int end = Math.Clamp(endExclusive, 0, _columns);
        for (int column = start; column < end; column++)
        {
            line.Cells[column] = CreateBlankCell(_currentStyle);
        }
    }

    private void PutChar(char ch)
    {
        if (_cursorColumn >= _columns)
        {
            _cursorColumn = 0;
            MoveDownAndScrollIfNeeded();
        }

        _screen[_cursorRow].Cells[_cursorColumn] = new TerminalCell(ch, _currentStyle);
        _cursorColumn++;
    }

    private void MoveDownAndScrollIfNeeded()
    {
        if (_cursorRow == _scrollBottom)
        {
            ScrollUpRegion(1, _scrollTop, _scrollBottom);
            return;
        }

        _cursorRow = Math.Min(_rows - 1, _cursorRow + 1);
    }

    private void ScrollUpRegion(int lines, int top, int bottom)
    {
        int count = Math.Clamp(lines, 1, bottom - top + 1);
        bool appendToScrollback = _primaryScreenBackup is null && top == 0 && bottom == _rows - 1;
        for (int row = 0; row < count; row++)
        {
            if (appendToScrollback)
            {
                AppendScrollback(CloneLine(_screen[top + row]));
            }
        }

        for (int row = top; row <= bottom - count; row++)
        {
            _screen[row] = _screen[row + count];
        }

        for (int row = bottom - count + 1; row <= bottom; row++)
        {
            _screen[row] = new TerminalLine(_columns, _currentStyle);
        }
    }

    private void ScrollDownRegion(int lines, int top, int bottom)
    {
        int count = Math.Clamp(lines, 1, bottom - top + 1);
        for (int row = bottom; row >= top + count; row--)
        {
            _screen[row] = _screen[row - count];
        }

        for (int row = top; row < top + count; row++)
        {
            _screen[row] = new TerminalLine(_columns, _currentStyle);
        }
    }

    private int FindLastVisibleScreenRow(bool showCursor)
    {
        int lastNonEmptyRow = 0;
        for (int row = _rows - 1; row >= 0; row--)
        {
            if (!IsLineBlank(_screen[row]))
            {
                lastNonEmptyRow = row;
                break;
            }
        }

        if (showCursor && _cursorVisible)
        {
            lastNonEmptyRow = Math.Max(lastNonEmptyRow, _cursorRow);
        }

        return lastNonEmptyRow;
    }

    private static bool IsLineBlank(TerminalLine line)
    {
        foreach (TerminalCell cell in line.Cells)
        {
            if (cell.Character != ' ' || cell.Style != TerminalStyle.Default)
            {
                return false;
            }
        }

        return true;
    }

    private void AppendLine(InlineCollection inlines, TerminalLine line, int cursorColumn, bool showCursor, ref bool isFirstLine)
    {
        if (!isFirstLine)
        {
            inlines.Add(new LineBreak());
        }

        isFirstLine = false;
        int visibleLength = FindVisibleLength(line, cursorColumn);
        if (visibleLength == 0)
        {
            return;
        }

        var text = new StringBuilder();
        ResolvedStyle? currentStyle = null;
        for (int column = 0; column < visibleLength; column++)
        {
            TerminalCell cell = line.Cells[column];
            bool isCursor = showCursor && cursorColumn == column;
            ResolvedStyle style = ResolveStyle(cell.Style, isCursor);
            if (currentStyle is null || currentStyle.Value != style)
            {
                FlushRun(inlines, text, currentStyle);
                currentStyle = style;
            }

            text.Append(cell.Character);
        }

        FlushRun(inlines, text, currentStyle);
    }

    private static int FindVisibleLength(TerminalLine line, int cursorColumn)
    {
        for (int column = line.Cells.Length - 1; column >= 0; column--)
        {
            TerminalCell cell = line.Cells[column];
            if (column == cursorColumn || cell.Character != ' ' || cell.Style != TerminalStyle.Default)
            {
                return column + 1;
            }
        }

        return cursorColumn >= 0 ? cursorColumn + 1 : 0;
    }

    private static void FlushRun(InlineCollection inlines, StringBuilder text, ResolvedStyle? style)
    {
        if (text.Length == 0 || style is null)
        {
            return;
        }

        var run = new Run(text.ToString())
        {
            Foreground = GetBrush(style.Value.Foreground),
            Background = GetBrush(style.Value.Background),
            FontWeight = style.Value.Bold ? FontWeights.SemiBold : FontWeights.Regular
        };

        if (style.Value.Underline)
        {
            run.TextDecorations = TextDecorations.Underline;
        }

        inlines.Add(run);
        text.Clear();
    }

    private static ResolvedStyle ResolveStyle(TerminalStyle style, bool isCursor)
    {
        Color foreground = style.Foreground ?? DefaultForeground;
        Color background = style.Background ?? DefaultBackground;

        if (style.Inverse)
        {
            (foreground, background) = (background, foreground);
        }

        if (isCursor)
        {
            (foreground, background) = (background, foreground);
            if (foreground == background)
            {
                background = CursorAccent;
                foreground = DefaultBackground;
            }
        }

        return new ResolvedStyle(foreground, background, style.Bold, style.Underline);
    }

    private static TerminalCell CreateBlankCell(TerminalStyle style)
    {
        return new TerminalCell(' ', style);
    }

    private static int?[] ParseParameters(string paramText)
    {
        if (string.IsNullOrEmpty(paramText))
        {
            return Array.Empty<int?>();
        }

        string[] parts = paramText.Split(';');
        var result = new int?[parts.Length];
        for (int index = 0; index < parts.Length; index++)
        {
            if (int.TryParse(parts[index], out int value))
            {
                result[index] = value;
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
        return !value.HasValue || value.Value == 0 ? defaultValue : value.Value;
    }

    private enum ParserState
    {
        Normal,
        Escape,
        Csi,
        Osc,
        OscEscape
    }

    private sealed class TerminalLine
    {
        public TerminalLine(int columns, TerminalStyle blankStyle)
        {
            Cells = new TerminalCell[columns];
            for (int index = 0; index < columns; index++)
            {
                Cells[index] = CreateBlankCell(blankStyle);
            }
        }

        public TerminalCell[] Cells { get; }
    }

    private sealed record ScreenState(
        List<TerminalLine> Screen,
        int CursorRow,
        int CursorColumn,
        int SavedCursorRow,
        int SavedCursorColumn,
        int ScrollTop,
        int ScrollBottom,
        TerminalStyle Style,
        TerminalStyle SavedStyle);

    private readonly record struct TerminalCell(char Character, TerminalStyle Style);

    private readonly record struct TerminalStyle(
        Color? Foreground,
        Color? Background,
        bool Bold,
        bool Underline,
        bool Inverse)
    {
        public static readonly TerminalStyle Default = new(null, null, false, false, false);
    }

    private readonly record struct ResolvedStyle(
        Color Foreground,
        Color Background,
        bool Bold,
        bool Underline);
}
