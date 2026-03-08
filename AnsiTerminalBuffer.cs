using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Globalization;

namespace ConPtyTerminal;

internal enum TerminalMouseTrackingMode
{
    Off,
    X10,
    ButtonEvent,
    AnyEvent
}

internal enum TerminalMouseEncoding
{
    Default,
    Utf8,
    Sgr,
    Urxvt
}

internal enum TerminalCharacterSet
{
    Ascii,
    DecSpecialGraphics
}

internal enum TerminalCursorShape
{
    Block,
    Underline,
    Bar
}

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
    private static readonly char[] CsiIntermediateCharacters = [' ', '!', '"', '#', '$', '%', '&', '\'', '(', ')', '*', '+', ',', '-', '.', '/'];

    private readonly int _scrollbackLimit;
    private readonly List<TerminalLine> _scrollback = [];
    private readonly List<TerminalRenderLineSnapshot> _scrollbackRenderCache = [];
    private readonly StringBuilder _csiBuffer = new();
    private readonly StringBuilder _oscBuffer = new();
    private readonly StringBuilder _pendingClusterText = new();

    private List<TerminalLine> _screen;
    private TerminalRenderLineSnapshot[] _screenRenderCache;
    private TerminalRenderLineSnapshot[] _combinedRenderCache = [];
    private bool[] _tabStops;
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
    private bool _cursorBlinkEnabled = true;
    private int _charsetDesignationTarget;
    private bool _applicationCursorKeys;
    private bool _applicationKeypad;
    private bool _insertMode;
    private bool _originMode;
    private bool _autoWrapEnabled = true;
    private bool _alternateScrollEnabled;
    private bool _bracketedPasteEnabled;
    private bool _focusReportingEnabled;
    private bool _useG1CharacterSet;
    private bool _savedUseG1CharacterSet;
    private bool _savedInsertMode;
    private bool _savedOriginMode;
    private bool _savedAutoWrapEnabled = true;
    private bool _useUtf8MouseEncoding;
    private bool _useSgrMouseEncoding;
    private bool _useUrxvtMouseEncoding;
    private TerminalMouseTrackingMode _mouseTrackingMode;
    private TerminalCursorShape _cursorShape = TerminalCursorShape.Block;
    private TerminalCharacterSet _g0CharacterSet = TerminalCharacterSet.Ascii;
    private TerminalCharacterSet _g1CharacterSet = TerminalCharacterSet.Ascii;
    private TerminalCharacterSet _savedG0CharacterSet = TerminalCharacterSet.Ascii;
    private TerminalCharacterSet _savedG1CharacterSet = TerminalCharacterSet.Ascii;
    private string? _currentHyperlink;
    private string? _savedHyperlink;
    private string _windowTitle = string.Empty;
    private string _lastPrintedClusterText = string.Empty;
    private int _lastPrintedClusterWidth;
    private int _pendingClusterWidth;
    private bool _pendingClusterJoinNext;
    private int _pendingClusterRegionalIndicatorCount;
    private bool _renderCacheDirty = true;
    private bool _screenRenderCacheDirty = true;
    private bool _cachedRenderShowCursor;
    private int _cachedVisibleScreenRow = -1;

    public event EventHandler<string>? InputSequenceGenerated;
    public event EventHandler<string>? ClipboardSetRequested;
    public event EventHandler<string>? ClipboardQueryRequested;

    public AnsiTerminalBuffer(short columns, short rows, int scrollbackLimit = DefaultScrollbackLimit)
    {
        _scrollbackLimit = Math.Max(scrollbackLimit, rows);
        _columns = Math.Max(columns, (short)MinColumns);
        _rows = Math.Max(rows, (short)MinRows);
        _screen = CreateScreen(_rows, _columns, TerminalStyle.Default);
        _screenRenderCache = new TerminalRenderLineSnapshot[_rows];
        _tabStops = CreateDefaultTabStops(_columns);
        ResetMargins();
    }

    public string WindowTitle => _windowTitle;
    public bool ApplicationCursorKeysEnabled => _applicationCursorKeys;
    public bool ApplicationKeypadEnabled => _applicationKeypad;
    public bool AlternateScrollEnabled => _alternateScrollEnabled;
    public bool BracketedPasteEnabled => _bracketedPasteEnabled;
    public int CursorRow => _cursorRow;
    public int CursorColumn => Math.Clamp(_cursorColumn, 0, _columns - 1);
    public bool CursorBlinkEnabled => _cursorBlinkEnabled;
    public TerminalCursorShape CursorShape => _cursorShape;
    public bool CursorVisible => _cursorVisible;
    public bool FocusReportingEnabled => _focusReportingEnabled;
    public bool IsAlternateScreenActive => _primaryScreenBackup is not null;
    public TerminalMouseEncoding MouseEncoding => ResolveMouseEncoding();
    public TerminalMouseTrackingMode MouseTrackingMode => _mouseTrackingMode;
    public int ScrollbackLineCount => _scrollback.Count;
    public int VisibleLineCount => FindLastVisibleScreenRow(showCursor: false) + 1;

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

        bool[] resizedTabStops = CreateDefaultTabStops(newColumns);
        Array.Copy(_tabStops, resizedTabStops, Math.Min(_tabStops.Length, resizedTabStops.Length));

        _screen = resizedScreen;
        _tabStops = resizedTabStops;
        _columns = newColumns;
        _rows = newRows;
        _cursorRow = Math.Clamp(_cursorRow - sourceStartRow + targetStartRow, 0, _rows - 1);
        _cursorColumn = Math.Clamp(_cursorColumn, 0, _columns - 1);
        _savedCursorRow = Math.Clamp(_savedCursorRow, 0, _rows - 1);
        _savedCursorColumn = Math.Clamp(_savedCursorColumn, 0, _columns - 1);
        ResetMargins();
        ResetScreenRenderCache();
    }

    public void Process(string text)
    {
        for (int index = 0; index < text.Length;)
        {
            if (_state == ParserState.Normal &&
                Rune.TryGetRuneAt(text, index, out Rune rune) &&
                !IsControlRune(rune))
            {
                ProcessRune(rune);
                index += rune.Utf16SequenceLength;
                continue;
            }

            FlushPendingCluster();
            ProcessChar(text[index]);
            index++;
        }

        FlushPendingCluster();
        InvalidateScreenRenderCache();
    }

    public TerminalRenderSnapshot CreateRenderSnapshot(bool showCursor)
    {
        if (_cachedRenderShowCursor != showCursor)
        {
            _cachedRenderShowCursor = showCursor;
            InvalidateScreenRenderCache();
        }

        if (_screenRenderCacheDirty)
        {
            RebuildScreenRenderCache(showCursor);
        }

        int lastScreenRow = FindLastVisibleScreenRow(showCursor);
        if (_cachedVisibleScreenRow != lastScreenRow)
        {
            _cachedVisibleScreenRow = lastScreenRow;
            _renderCacheDirty = true;
        }

        int visibleScreenLineCount = lastScreenRow + 1;
        int totalLineCount = _scrollbackRenderCache.Count + visibleScreenLineCount;
        if (_renderCacheDirty || _combinedRenderCache.Length != totalLineCount)
        {
            _combinedRenderCache = new TerminalRenderLineSnapshot[totalLineCount];
            if (_scrollbackRenderCache.Count > 0)
            {
                _scrollbackRenderCache.CopyTo(_combinedRenderCache, 0);
            }

            if (visibleScreenLineCount > 0)
            {
                Array.Copy(
                    _screenRenderCache,
                    0,
                    _combinedRenderCache,
                    _scrollbackRenderCache.Count,
                    visibleScreenLineCount);
            }

            _renderCacheDirty = false;
        }

        return new TerminalRenderSnapshot(_combinedRenderCache);
    }

    public TerminalDocumentSnapshot CreateDocument(FontFamily fontFamily, double fontSize, bool showCursor)
    {
        var document = new FlowDocument
        {
            FontFamily = fontFamily,
            FontSize = fontSize,
            Background = GetBrush(DefaultBackground),
            TextAlignment = TextAlignment.Left
        };

        var paragraph = new Paragraph();
        FrameworkElement? cursorAnchor = null;

        bool isFirstLine = true;
        foreach (TerminalRenderLineSnapshot lineSnapshot in CreateRenderSnapshot(showCursor).Lines)
        {
            AppendLineSnapshot(paragraph.Inlines, lineSnapshot, ref isFirstLine, ref cursorAnchor);
        }

        if (paragraph.Inlines.Count == 0)
        {
            paragraph.Inlines.Add(new Run(string.Empty));
        }

        document.Blocks.Add(paragraph);
        return new TerminalDocumentSnapshot(document, cursorAnchor);
    }

    public void ClearScrollback()
    {
        _scrollback.Clear();
        _scrollbackRenderCache.Clear();
        _renderCacheDirty = true;
    }

    public string CreatePlainTextSnapshot()
    {
        var builder = new StringBuilder();
        bool isFirstLine = true;
        foreach (TerminalLine line in _scrollback)
        {
            AppendPlainTextLine(builder, line, ref isFirstLine);
        }

        int lastScreenRow = FindLastVisibleScreenRow(showCursor: false);
        for (int row = 0; row <= lastScreenRow; row++)
        {
            AppendPlainTextLine(builder, _screen[row], ref isFirstLine);
        }

        return builder.ToString();
    }

    internal string GetScreenLineText(int row)
    {
        if (row < 0 || row >= _screen.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(row));
        }

        return ExtractLineText(_screen[row]);
    }

    internal string? GetCellHyperlink(int row, int column)
    {
        if (row < 0 || row >= _screen.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(row));
        }

        if (column < 0 || column >= _screen[row].Cells.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(column));
        }

        return _screen[row].Cells[column].Hyperlink;
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

    internal static SolidColorBrush GetBrush(Color color)
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
        _scrollbackRenderCache.Add(CreateLineSnapshot(line, -1, -1, showCursor: false));
        _renderCacheDirty = true;
        int overflow = _scrollback.Count - _scrollbackLimit;
        if (overflow > 0)
        {
            _scrollback.RemoveRange(0, overflow);
            _scrollbackRenderCache.RemoveRange(0, overflow);
        }
    }

    private void ResetMargins()
    {
        _scrollTop = 0;
        _scrollBottom = _rows - 1;
    }

    private void ResetTabStops()
    {
        _tabStops = CreateDefaultTabStops(_columns);
    }

    private void InvalidateScreenRenderCache()
    {
        _screenRenderCacheDirty = true;
        _renderCacheDirty = true;
    }

    private void ResetScreenRenderCache()
    {
        _screenRenderCache = new TerminalRenderLineSnapshot[_rows];
        _cachedVisibleScreenRow = -1;
        InvalidateScreenRenderCache();
    }

    private void RebuildScreenRenderCache(bool showCursor)
    {
        if (_screenRenderCache.Length != _rows)
        {
            _screenRenderCache = new TerminalRenderLineSnapshot[_rows];
        }

        for (int row = 0; row < _rows; row++)
        {
            int cursorColumn = showCursor && _cursorVisible && row == _cursorRow ? _cursorColumn : -1;
            int anchorColumn = row == _cursorRow ? _cursorColumn : -1;
            _screenRenderCache[row] = CreateLineSnapshot(_screen[row], cursorColumn, anchorColumn, showCursor);
        }

        _screenRenderCacheDirty = false;
    }

    private void ResetTerminal()
    {
        ClearScrollback();
        _screen = CreateScreen(_rows, _columns, TerminalStyle.Default);
        _primaryScreenBackup = null;
        _cursorRow = 0;
        _cursorColumn = 0;
        _savedCursorRow = 0;
        _savedCursorColumn = 0;
        _currentStyle = TerminalStyle.Default;
        _savedStyle = TerminalStyle.Default;
        _cursorVisible = true;
        _cursorBlinkEnabled = true;
        _cursorShape = TerminalCursorShape.Block;
        _charsetDesignationTarget = 0;
        _applicationCursorKeys = false;
        _applicationKeypad = false;
        _insertMode = false;
        _originMode = false;
        _autoWrapEnabled = true;
        _alternateScrollEnabled = false;
        _bracketedPasteEnabled = false;
        _focusReportingEnabled = false;
        _useG1CharacterSet = false;
        _savedUseG1CharacterSet = false;
        _savedInsertMode = false;
        _savedOriginMode = false;
        _savedAutoWrapEnabled = true;
        _useUtf8MouseEncoding = false;
        _useSgrMouseEncoding = false;
        _useUrxvtMouseEncoding = false;
        _mouseTrackingMode = TerminalMouseTrackingMode.Off;
        _g0CharacterSet = TerminalCharacterSet.Ascii;
        _g1CharacterSet = TerminalCharacterSet.Ascii;
        _savedG0CharacterSet = TerminalCharacterSet.Ascii;
        _savedG1CharacterSet = TerminalCharacterSet.Ascii;
        _currentHyperlink = null;
        _savedHyperlink = null;
        _windowTitle = string.Empty;
        _lastPrintedClusterText = string.Empty;
        _lastPrintedClusterWidth = 0;
        ClearPendingCluster();
        _state = ParserState.Normal;
        _csiBuffer.Clear();
        _oscBuffer.Clear();
        ResetTabStops();
        ResetMargins();
        ResetScreenRenderCache();
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
            case ParserState.Charset:
                ProcessCharsetDesignation(ch);
                break;
        }
    }

    private void ProcessNormal(char ch)
    {
        switch (ch)
        {
            case '\u0007':
                return;
            case '\u000E':
                _useG1CharacterSet = true;
                return;
            case '\u000F':
                _useG1CharacterSet = false;
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
                _cursorColumn = FindNextTabStop(_cursorColumn);

                return;
            default:
                return;
        }
    }

    private void ProcessRune(Rune rune)
    {
        Rune mappedRune = MapActiveRune(rune);
        int width = GetDisplayWidth(mappedRune);
        if (width <= 0)
        {
            AppendClusterExtension(mappedRune);
            return;
        }

        if (_pendingClusterText.Length == 0)
        {
            if (TryExtendPreviousCluster(mappedRune, width))
            {
                return;
            }

            StartPendingCluster(mappedRune, width);
            return;
        }

        if (ShouldAppendToPendingCluster(mappedRune))
        {
            AppendPendingClusterRune(mappedRune, width);
            return;
        }

        FlushPendingCluster();
        if (TryExtendPreviousCluster(mappedRune, width))
        {
            return;
        }

        StartPendingCluster(mappedRune, width);
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
            case '(':
                _charsetDesignationTarget = 0;
                _state = ParserState.Charset;
                return;
            case ')':
                _charsetDesignationTarget = 1;
                _state = ParserState.Charset;
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
            case 'H':
                SetTabStopAtCursor();
                _state = ParserState.Normal;
                return;
            case '=':
                _applicationKeypad = true;
                _state = ParserState.Normal;
                return;
            case '>':
                _applicationKeypad = false;
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

    private void ProcessCharsetDesignation(char ch)
    {
        TerminalCharacterSet characterSet = ch switch
        {
            '0' => TerminalCharacterSet.DecSpecialGraphics,
            _ => TerminalCharacterSet.Ascii
        };

        if (_charsetDesignationTarget == 0)
        {
            _g0CharacterSet = characterSet;
        }
        else
        {
            _g1CharacterSet = characterSet;
        }

        _state = ParserState.Normal;
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
            return;
        }

        if (command == "8")
        {
            DispatchOscHyperlink(value);
            return;
        }

        if (command == "52")
        {
            DispatchOscClipboard(value);
        }
    }

    private void DispatchOscHyperlink(string value)
    {
        int separatorIndex = value.IndexOf(';');
        if (separatorIndex < 0)
        {
            return;
        }

        string uri = value[(separatorIndex + 1)..];
        _currentHyperlink = string.IsNullOrEmpty(uri) ? null : uri;
    }

    private void DispatchOscClipboard(string value)
    {
        int separatorIndex = value.IndexOf(';');
        if (separatorIndex < 0)
        {
            return;
        }

        string selectionTargets = value[..separatorIndex];
        string payload = value[(separatorIndex + 1)..];
        if (payload == "?")
        {
            ClipboardQueryRequested?.Invoke(this, string.IsNullOrEmpty(selectionTargets) ? "c" : selectionTargets);
            return;
        }

        if (payload.Length == 0)
        {
            ClipboardSetRequested?.Invoke(this, string.Empty);
            return;
        }

        try
        {
            byte[] decoded = Convert.FromBase64String(NormalizeBase64(payload));
            string text = Encoding.UTF8.GetString(decoded);
            ClipboardSetRequested?.Invoke(this, text);
        }
        catch (FormatException)
        {
        }
    }

    private void DispatchCsi(char command, string rawParams)
    {
        char prefix = rawParams.Length > 0 && (rawParams[0] == '?' || rawParams[0] == '>') ? rawParams[0] : '\0';
        bool isPrivate = prefix == '?';
        bool isSecondary = prefix == '>';
        string parameterSection = prefix == '\0' ? rawParams : rawParams[1..];
        int intermediateIndex = parameterSection.IndexOfAny(CsiIntermediateCharacters);
        string intermediate = intermediateIndex >= 0 ? parameterSection[intermediateIndex..] : string.Empty;
        string paramText = intermediateIndex >= 0 ? parameterSection[..intermediateIndex] : parameterSection;
        int?[] parameters = ParseParameters(paramText);

        switch (command)
        {
            case '@':
                InsertCharacters(GetParameter(parameters, 0, 1));
                break;
            case 'A':
                _cursorRow = Math.Max(GetTopRowLimit(), _cursorRow - GetParameter(parameters, 0, 1));
                break;
            case 'B':
                _cursorRow = Math.Min(GetBottomRowLimit(), _cursorRow + GetParameter(parameters, 0, 1));
                break;
            case 'C':
                _cursorColumn = Math.Min(_columns - 1, _cursorColumn + GetParameter(parameters, 0, 1));
                break;
            case 'D':
                _cursorColumn = Math.Max(0, _cursorColumn - GetParameter(parameters, 0, 1));
                break;
            case 'E':
                _cursorRow = Math.Min(GetBottomRowLimit(), _cursorRow + GetParameter(parameters, 0, 1));
                _cursorColumn = 0;
                break;
            case 'F':
                _cursorRow = Math.Max(GetTopRowLimit(), _cursorRow - GetParameter(parameters, 0, 1));
                _cursorColumn = 0;
                break;
            case 'G':
                _cursorColumn = Math.Clamp(GetParameter(parameters, 0, 1) - 1, 0, _columns - 1);
                break;
            case 'H':
            case 'f':
                SetCursorPosition(GetParameter(parameters, 0, 1), GetParameter(parameters, 1, 1));
                break;
            case 'I':
                MoveToNextTabStop(GetParameter(parameters, 0, 1));
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
            case 'Z':
                MoveToPreviousTabStop(GetParameter(parameters, 0, 1));
                break;
            case 'b':
                RepeatLastPrintedCluster(GetParameter(parameters, 0, 1));
                break;
            case 'd':
                SetCursorRow(GetParameter(parameters, 0, 1));
                break;
            case 'c':
                DispatchDeviceAttributes(isPrivate, isSecondary);
                break;
            case 'g':
                ClearTabStops(GetParameter(parameters, 0, 0));
                break;
            case 'h':
            case 'l':
                if (isPrivate)
                {
                    SetPrivateMode(parameters, command == 'h');
                }
                else
                {
                    SetMode(parameters, command == 'h');
                }

                break;
            case 'm':
                ApplySgr(parameters);
                break;
            case 'n':
                DispatchDeviceStatusReport(parameters, isPrivate);
                break;
            case 'p':
                if (intermediate == "!")
                {
                    SoftResetTerminal();
                }

                break;
            case 'q':
                if (intermediate == " ")
                {
                    SetCursorStyle(GetParameter(parameters, 0, 0));
                }

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
        _savedUseG1CharacterSet = _useG1CharacterSet;
        _savedG0CharacterSet = _g0CharacterSet;
        _savedG1CharacterSet = _g1CharacterSet;
        _savedInsertMode = _insertMode;
        _savedOriginMode = _originMode;
        _savedAutoWrapEnabled = _autoWrapEnabled;
        _savedHyperlink = _currentHyperlink;
    }

    private void RestoreCursorState()
    {
        _cursorRow = Math.Clamp(_savedCursorRow, 0, _rows - 1);
        _cursorColumn = Math.Clamp(_savedCursorColumn, 0, _columns - 1);
        _currentStyle = _savedStyle;
        _useG1CharacterSet = _savedUseG1CharacterSet;
        _g0CharacterSet = _savedG0CharacterSet;
        _g1CharacterSet = _savedG1CharacterSet;
        _insertMode = _savedInsertMode;
        _originMode = _savedOriginMode;
        _autoWrapEnabled = _savedAutoWrapEnabled;
        _currentHyperlink = _savedHyperlink;
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

    private void DispatchDeviceAttributes(bool isPrivate, bool isSecondary)
    {
        if (isSecondary)
        {
            EmitInputSequence("\u001b[>0;10;1c");
            return;
        }

        if (isPrivate)
        {
            EmitInputSequence("\u001b[?1;2c");
            return;
        }

        EmitInputSequence("\u001b[?1;2c");
    }

    private void DispatchDeviceStatusReport(int?[] parameters, bool isPrivate)
    {
        int report = GetParameter(parameters, 0, 0);
        switch (report)
        {
            case 5:
                EmitInputSequence(isPrivate ? "\u001b[?0n" : "\u001b[0n");
                break;
            case 6:
                string prefix = isPrivate ? "?" : string.Empty;
                EmitInputSequence($"\u001b[{prefix}{_cursorRow + 1};{_cursorColumn + 1}R");
                break;
        }
    }

    private void EmitInputSequence(string text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            InputSequenceGenerated?.Invoke(this, text);
        }
    }

    private static string NormalizeBase64(string payload)
    {
        int remainder = payload.Length % 4;
        return remainder == 0
            ? payload
            : payload.PadRight(payload.Length + (4 - remainder), '=');
    }

    private TerminalMouseEncoding ResolveMouseEncoding()
    {
        if (_useSgrMouseEncoding)
        {
            return TerminalMouseEncoding.Sgr;
        }

        if (_useUrxvtMouseEncoding)
        {
            return TerminalMouseEncoding.Urxvt;
        }

        if (_useUtf8MouseEncoding)
        {
            return TerminalMouseEncoding.Utf8;
        }

        return TerminalMouseEncoding.Default;
    }

    private void SetPrivateMode(int?[] parameters, bool enabled)
    {
        foreach (int? parameter in parameters)
        {
            switch (parameter)
            {
                case 1:
                    _applicationCursorKeys = enabled;
                    break;
                case 6:
                    _originMode = enabled;
                    MoveCursorHome();
                    break;
                case 7:
                    _autoWrapEnabled = enabled;
                    if (!_autoWrapEnabled && _cursorColumn >= _columns)
                    {
                        _cursorColumn = _columns - 1;
                    }

                    break;
                case 25:
                    _cursorVisible = enabled;
                    break;
                case 1007:
                    _alternateScrollEnabled = enabled;
                    break;
                case 12:
                    _cursorBlinkEnabled = enabled;
                    break;
                case 47:
                case 1047:
                    if (enabled)
                    {
                        EnterAlternateScreen();
                    }
                    else
                    {
                        ExitAlternateScreen();
                    }

                    break;
                case 1048:
                    if (enabled)
                    {
                        SaveCursorState();
                    }
                    else
                    {
                        RestoreCursorState();
                    }

                    break;
                case 1049:
                    if (enabled)
                    {
                        SaveCursorState();
                        EnterAlternateScreen();
                    }
                    else
                    {
                        ExitAlternateScreen();
                        RestoreCursorState();
                    }

                    break;
                case 1000:
                    _mouseTrackingMode = enabled ? TerminalMouseTrackingMode.X10 : TerminalMouseTrackingMode.Off;
                    break;
                case 1002:
                    _mouseTrackingMode = enabled ? TerminalMouseTrackingMode.ButtonEvent : TerminalMouseTrackingMode.Off;
                    break;
                case 1003:
                    _mouseTrackingMode = enabled ? TerminalMouseTrackingMode.AnyEvent : TerminalMouseTrackingMode.Off;
                    break;
                case 1004:
                    _focusReportingEnabled = enabled;
                    break;
                case 1005:
                    _useUtf8MouseEncoding = enabled;
                    break;
                case 1006:
                    _useSgrMouseEncoding = enabled;
                    break;
                case 1015:
                    _useUrxvtMouseEncoding = enabled;
                    break;
                case 66:
                    _applicationKeypad = enabled;
                    break;
                case 2004:
                    _bracketedPasteEnabled = enabled;
                    break;
            }
        }
    }

    private void SetMode(int?[] parameters, bool enabled)
    {
        foreach (int? parameter in parameters)
        {
            switch (parameter)
            {
                case 4:
                    _insertMode = enabled;
                    break;
            }
        }
    }

    private void SetCursorStyle(int parameter)
    {
        switch (parameter)
        {
            case 0:
            case 1:
                _cursorShape = TerminalCursorShape.Block;
                _cursorBlinkEnabled = true;
                break;
            case 2:
                _cursorShape = TerminalCursorShape.Block;
                _cursorBlinkEnabled = false;
                break;
            case 3:
                _cursorShape = TerminalCursorShape.Underline;
                _cursorBlinkEnabled = true;
                break;
            case 4:
                _cursorShape = TerminalCursorShape.Underline;
                _cursorBlinkEnabled = false;
                break;
            case 5:
                _cursorShape = TerminalCursorShape.Bar;
                _cursorBlinkEnabled = true;
                break;
            case 6:
                _cursorShape = TerminalCursorShape.Bar;
                _cursorBlinkEnabled = false;
                break;
        }
    }

    private void SoftResetTerminal()
    {
        _currentStyle = TerminalStyle.Default;
        _cursorVisible = true;
        _cursorBlinkEnabled = true;
        _cursorShape = TerminalCursorShape.Block;
        _applicationCursorKeys = false;
        _applicationKeypad = false;
        _insertMode = false;
        _originMode = false;
        _autoWrapEnabled = true;
        _alternateScrollEnabled = false;
        _bracketedPasteEnabled = false;
        _focusReportingEnabled = false;
        _useG1CharacterSet = false;
        _useUtf8MouseEncoding = false;
        _useSgrMouseEncoding = false;
        _useUrxvtMouseEncoding = false;
        _mouseTrackingMode = TerminalMouseTrackingMode.Off;
        _g0CharacterSet = TerminalCharacterSet.Ascii;
        _g1CharacterSet = TerminalCharacterSet.Ascii;
        _currentHyperlink = null;
        ResetMargins();
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
            _savedStyle,
            _currentHyperlink,
            _savedHyperlink);

        _screen = CreateScreen(_rows, _columns, TerminalStyle.Default);
        _cursorRow = 0;
        _cursorColumn = 0;
        _savedCursorRow = 0;
        _savedCursorColumn = 0;
        _currentStyle = TerminalStyle.Default;
        _savedStyle = TerminalStyle.Default;
        _currentHyperlink = null;
        _savedHyperlink = null;
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
        _currentHyperlink = _primaryScreenBackup.CurrentHyperlink;
        _savedHyperlink = _primaryScreenBackup.SavedHyperlink;
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

    private static bool[] CreateDefaultTabStops(int columns)
    {
        var tabStops = new bool[Math.Max(columns, 0)];
        for (int column = 8; column < tabStops.Length; column += 8)
        {
            tabStops[column] = true;
        }

        return tabStops;
    }

    private int FindNextTabStop(int currentColumn)
    {
        for (int column = Math.Clamp(currentColumn + 1, 0, _columns - 1); column < _columns; column++)
        {
            if (_tabStops[column])
            {
                return column;
            }
        }

        return _columns - 1;
    }

    private int FindPreviousTabStop(int currentColumn)
    {
        for (int column = Math.Clamp(currentColumn - 1, 0, _columns - 1); column >= 0; column--)
        {
            if (_tabStops[column])
            {
                return column;
            }
        }

        return 0;
    }

    private void MoveToNextTabStop(int count)
    {
        int stopCount = Math.Max(count, 1);
        for (int index = 0; index < stopCount; index++)
        {
            _cursorColumn = FindNextTabStop(_cursorColumn);
        }
    }

    private void MoveToPreviousTabStop(int count)
    {
        int stopCount = Math.Max(count, 1);
        for (int index = 0; index < stopCount; index++)
        {
            _cursorColumn = FindPreviousTabStop(_cursorColumn);
        }
    }

    private void SetTabStopAtCursor()
    {
        if (_cursorColumn >= 0 && _cursorColumn < _tabStops.Length)
        {
            _tabStops[_cursorColumn] = true;
        }
    }

    private void ClearTabStops(int mode)
    {
        switch (mode)
        {
            case 0:
                if (_cursorColumn >= 0 && _cursorColumn < _tabStops.Length)
                {
                    _tabStops[_cursorColumn] = false;
                }

                break;
            case 3:
                Array.Fill(_tabStops, false);
                break;
        }
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

        MoveCursorHome();
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
                ClearScrollback();
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

    private void PutText(string text, int width)
    {
        int normalizedWidth = Math.Clamp(width, 1, 2);
        if (_cursorColumn >= _columns)
        {
            if (_autoWrapEnabled)
            {
                _cursorColumn = 0;
                MoveDownAndScrollIfNeeded();
            }
            else
            {
                _cursorColumn = _columns - 1;
            }
        }

        if (normalizedWidth == 2 && _cursorColumn == _columns - 1)
        {
            if (_autoWrapEnabled)
            {
                _cursorColumn = 0;
                MoveDownAndScrollIfNeeded();
            }
            else
            {
                return;
            }
        }

        TerminalLine line = _screen[_cursorRow];
        if (_insertMode)
        {
            InsertCharacters(normalizedWidth);
        }

        ClearWideOverlap(line, _cursorColumn);
        line.Cells[_cursorColumn] = new TerminalCell(text, _currentStyle, _currentHyperlink, IsContinuation: false, Width: normalizedWidth);

        if (normalizedWidth == 2)
        {
            if (_cursorColumn + 1 >= _columns)
            {
                _cursorColumn = _columns;
                return;
            }

            line.Cells[_cursorColumn + 1] = new TerminalCell(string.Empty, _currentStyle, _currentHyperlink, IsContinuation: true, Width: 0);
        }

        _cursorColumn += normalizedWidth;
        _lastPrintedClusterText = text;
        _lastPrintedClusterWidth = normalizedWidth;
        if (!_autoWrapEnabled && _cursorColumn >= _columns)
        {
            _cursorColumn = _columns - 1;
        }
    }

    private void AppendCombiningRune(Rune rune)
    {
        int targetColumn = _cursorColumn > 0 ? _cursorColumn - 1 : FindPreviousOccupiedColumn();
        if (targetColumn < 0)
        {
            return;
        }

        TerminalLine line = _screen[_cursorRow];
        while (targetColumn > 0 && line.Cells[targetColumn].IsContinuation)
        {
            targetColumn--;
        }

        TerminalCell cell = line.Cells[targetColumn];
        line.Cells[targetColumn] = cell with { Text = cell.Text + rune.ToString() };
        _lastPrintedClusterText = line.Cells[targetColumn].Text;
        _lastPrintedClusterWidth = Math.Max(1, cell.Width);
    }

    private void AppendClusterExtension(Rune rune)
    {
        if (_pendingClusterText.Length > 0)
        {
            AppendPendingClusterRune(rune, width: 0);
            return;
        }

        AppendCombiningRune(rune);
    }

    private void StartPendingCluster(Rune rune, int width)
    {
        ClearPendingCluster();
        _pendingClusterText.Append(rune.ToString());
        _pendingClusterWidth = Math.Clamp(width, 1, 2);
        _pendingClusterJoinNext = false;
        _pendingClusterRegionalIndicatorCount = IsRegionalIndicator(rune) ? 1 : 0;
    }

    private void AppendPendingClusterRune(Rune rune, int width)
    {
        _pendingClusterText.Append(rune.ToString());
        if (width > 0)
        {
            _pendingClusterWidth = Math.Max(_pendingClusterWidth, Math.Clamp(width, 1, 2));
        }

        _pendingClusterJoinNext = IsZeroWidthJoiner(rune);
        _pendingClusterRegionalIndicatorCount = IsRegionalIndicator(rune)
            ? _pendingClusterRegionalIndicatorCount + 1
            : 0;
    }

    private bool ShouldAppendToPendingCluster(Rune rune)
    {
        return _pendingClusterJoinNext ||
            (IsRegionalIndicator(rune) && _pendingClusterRegionalIndicatorCount == 1);
    }

    private void FlushPendingCluster()
    {
        if (_pendingClusterText.Length == 0)
        {
            return;
        }

        PutText(_pendingClusterText.ToString(), _pendingClusterWidth);
        ClearPendingCluster();
    }

    private void ClearPendingCluster()
    {
        _pendingClusterText.Clear();
        _pendingClusterWidth = 0;
        _pendingClusterJoinNext = false;
        _pendingClusterRegionalIndicatorCount = 0;
    }

    private bool TryExtendPreviousCluster(Rune rune, int width)
    {
        int targetColumn = FindPreviousClusterColumn();
        if (targetColumn < 0)
        {
            return false;
        }

        TerminalLine line = _screen[_cursorRow];
        TerminalCell cell = line.Cells[targetColumn];
        if (!ShouldExtendRenderedCluster(cell.Text, rune))
        {
            return false;
        }

        int normalizedWidth = Math.Clamp(Math.Max(cell.Width, width), 1, 2);
        line.Cells[targetColumn] = cell with
        {
            Text = cell.Text + rune.ToString(),
            Width = normalizedWidth
        };
        _lastPrintedClusterText = line.Cells[targetColumn].Text;
        _lastPrintedClusterWidth = normalizedWidth;

        if (cell.Width == 1 && normalizedWidth == 2 && targetColumn + 1 < _columns)
        {
            line.Cells[targetColumn + 1] = new TerminalCell(
                string.Empty,
                cell.Style,
                cell.Hyperlink,
                IsContinuation: true,
                Width: 0);
            _cursorColumn = Math.Max(_cursorColumn, targetColumn + 2);
        }

        return true;
    }

    private void RepeatLastPrintedCluster(int count)
    {
        if (string.IsNullOrEmpty(_lastPrintedClusterText))
        {
            return;
        }

        int repeatCount = Math.Max(count, 1);
        for (int index = 0; index < repeatCount; index++)
        {
            PutText(_lastPrintedClusterText, _lastPrintedClusterWidth);
        }
    }

    private int FindPreviousClusterColumn()
    {
        if (_cursorColumn <= 0)
        {
            return -1;
        }

        int targetColumn = Math.Min(_cursorColumn - 1, _columns - 1);
        TerminalLine line = _screen[_cursorRow];
        while (targetColumn > 0 && line.Cells[targetColumn].IsContinuation)
        {
            targetColumn--;
        }

        TerminalCell cell = line.Cells[targetColumn];
        return string.IsNullOrEmpty(cell.Text) || cell.Text == " " ? -1 : targetColumn;
    }

    private static bool ShouldExtendRenderedCluster(string text, Rune rune)
    {
        return EndsWithZeroWidthJoiner(text) ||
            (IsRegionalIndicator(rune) && CountRegionalIndicators(text) == 1);
    }

    private static bool EndsWithZeroWidthJoiner(string text)
    {
        return TryGetLastRune(text, out Rune lastRune) && IsZeroWidthJoiner(lastRune);
    }

    private static int CountRegionalIndicators(string text)
    {
        int count = 0;
        for (int index = 0; index < text.Length;)
        {
            if (!Rune.TryGetRuneAt(text, index, out Rune rune))
            {
                break;
            }

            if (IsRegionalIndicator(rune))
            {
                count++;
            }

            index += rune.Utf16SequenceLength;
        }

        return count;
    }

    private static bool TryGetLastRune(string text, out Rune rune)
    {
        for (int index = text.Length - 1; index >= 0; index--)
        {
            if (Rune.TryGetRuneAt(text, index, out rune))
            {
                return true;
            }
        }

        rune = default;
        return false;
    }

    private int FindPreviousOccupiedColumn()
    {
        TerminalLine line = _screen[_cursorRow];
        for (int column = _columns - 1; column >= 0; column--)
        {
            if (!string.IsNullOrEmpty(line.Cells[column].Text) && line.Cells[column].Text != " ")
            {
                return column;
            }
        }

        return -1;
    }

    private void ClearWideOverlap(TerminalLine line, int column)
    {
        if (column > 0 && line.Cells[column].IsContinuation)
        {
            line.Cells[column - 1] = CreateBlankCell(_currentStyle);
            line.Cells[column] = CreateBlankCell(_currentStyle);
        }

        if (column + 1 < _columns && line.Cells[column + 1].IsContinuation && !line.Cells[column].IsContinuation)
        {
            line.Cells[column] = CreateBlankCell(_currentStyle);
            line.Cells[column + 1] = CreateBlankCell(_currentStyle);
        }
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

    private int GetTopRowLimit()
    {
        return _originMode ? _scrollTop : 0;
    }

    private int GetBottomRowLimit()
    {
        return _originMode ? _scrollBottom : _rows - 1;
    }

    private void MoveCursorHome()
    {
        _cursorRow = GetTopRowLimit();
        _cursorColumn = 0;
    }

    private void SetCursorPosition(int rowParameter, int columnParameter)
    {
        int rowOffset = Math.Max(rowParameter, 1) - 1;
        int baseRow = _originMode ? _scrollTop : 0;
        int maxRow = _originMode ? _scrollBottom : _rows - 1;
        _cursorRow = Math.Clamp(baseRow + rowOffset, baseRow, maxRow);
        _cursorColumn = Math.Clamp(Math.Max(columnParameter, 1) - 1, 0, _columns - 1);
    }

    private void SetCursorRow(int rowParameter)
    {
        int rowOffset = Math.Max(rowParameter, 1) - 1;
        int baseRow = _originMode ? _scrollTop : 0;
        int maxRow = _originMode ? _scrollBottom : _rows - 1;
        _cursorRow = Math.Clamp(baseRow + rowOffset, baseRow, maxRow);
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
            if ((!cell.IsContinuation && cell.Text != " ") || cell.Style != TerminalStyle.Default)
            {
                return false;
            }

            if (cell.Hyperlink is not null)
            {
                return false;
            }
        }

        return true;
    }

    private static string ExtractLineText(TerminalLine line)
    {
        var builder = new StringBuilder(line.Cells.Length);
        foreach (TerminalCell cell in line.Cells)
        {
            if (!cell.IsContinuation)
            {
                builder.Append(cell.Text);
            }
        }

        return builder.ToString();
    }

    private static void AppendPlainTextLine(StringBuilder builder, TerminalLine line, ref bool isFirstLine)
    {
        if (!isFirstLine)
        {
            builder.AppendLine();
        }

        isFirstLine = false;
        builder.Append(ExtractLineText(line).TrimEnd());
    }

    private TerminalRenderLineSnapshot CreateLineSnapshot(TerminalLine line, int cursorColumn, int anchorColumn, bool showCursor)
    {
        int visibleLength = FindVisibleLength(line, cursorColumn);
        if (visibleLength == 0)
        {
            return new TerminalRenderLineSnapshot(
                anchorColumn == 0 ? 0 : -1,
                0,
                Array.Empty<TerminalRenderSegmentSnapshot>());
        }

        var text = new StringBuilder();
        var segments = new List<TerminalRenderSegmentSnapshot>();
        ResolvedStyle? currentStyle = null;
        int currentSegmentCellLength = 0;
        int anchorSegmentIndex = -1;
        for (int column = 0; column < visibleLength; column++)
        {
            if (anchorColumn == column)
            {
                FlushSegment(segments, text, currentStyle, ref currentSegmentCellLength);
                anchorSegmentIndex = segments.Count;
            }

            TerminalCell cell = line.Cells[column];
            if (cell.IsContinuation)
            {
                continue;
            }

            bool isCursor = showCursor && cursorColumn == column;
            ResolvedStyle style = ResolveStyle(cell.Style, cell.Hyperlink, isCursor);
            if (currentStyle is null || currentStyle.Value != style)
            {
                FlushSegment(segments, text, currentStyle, ref currentSegmentCellLength);
                currentStyle = style;
            }

            text.Append(cell.Text);
            currentSegmentCellLength += Math.Max(1, cell.Width);
        }

        FlushSegment(segments, text, currentStyle, ref currentSegmentCellLength);
        if (anchorColumn == visibleLength)
        {
            anchorSegmentIndex = segments.Count;
        }

        return new TerminalRenderLineSnapshot(anchorSegmentIndex, visibleLength, segments.ToArray());
    }

    private static void AppendLineSnapshot(InlineCollection inlines, TerminalRenderLineSnapshot lineSnapshot, ref bool isFirstLine, ref FrameworkElement? cursorAnchor)
    {
        if (!isFirstLine)
        {
            inlines.Add(new LineBreak());
        }

        isFirstLine = false;
        if (lineSnapshot.Segments.Length == 0)
        {
            if (lineSnapshot.AnchorSegmentIndex == 0)
            {
                InsertCursorAnchor(inlines, ref cursorAnchor);
            }

            return;
        }

        for (int index = 0; index < lineSnapshot.Segments.Length; index++)
        {
            if (lineSnapshot.AnchorSegmentIndex == index)
            {
                InsertCursorAnchor(inlines, ref cursorAnchor);
            }

            AppendSegment(inlines, lineSnapshot.Segments[index]);
        }

        if (lineSnapshot.AnchorSegmentIndex == lineSnapshot.Segments.Length)
        {
            InsertCursorAnchor(inlines, ref cursorAnchor);
        }
    }

    private void AppendLine(InlineCollection inlines, TerminalLine line, int cursorColumn, int anchorColumn, bool showCursor, ref bool isFirstLine, ref FrameworkElement? cursorAnchor)
    {
        if (!isFirstLine)
        {
            inlines.Add(new LineBreak());
        }

        isFirstLine = false;
        int visibleLength = FindVisibleLength(line, cursorColumn);
        if (visibleLength == 0)
        {
            if (anchorColumn == 0)
            {
                InsertCursorAnchor(inlines, ref cursorAnchor);
            }

            return;
        }

        var text = new StringBuilder();
        ResolvedStyle? currentStyle = null;
        for (int column = 0; column < visibleLength; column++)
        {
            if (anchorColumn == column)
            {
                FlushRun(inlines, text, currentStyle);
                InsertCursorAnchor(inlines, ref cursorAnchor);
            }

            TerminalCell cell = line.Cells[column];
            if (cell.IsContinuation)
            {
                continue;
            }

            bool isCursor = showCursor && cursorColumn == column;
            ResolvedStyle style = ResolveStyle(cell.Style, cell.Hyperlink, isCursor);
            if (currentStyle is null || currentStyle.Value != style)
            {
                FlushRun(inlines, text, currentStyle);
                currentStyle = style;
            }

            text.Append(cell.Text);
        }

        FlushRun(inlines, text, currentStyle);
        if (anchorColumn == visibleLength)
        {
            InsertCursorAnchor(inlines, ref cursorAnchor);
        }
    }

    private static int FindVisibleLength(TerminalLine line, int cursorColumn)
    {
        for (int column = line.Cells.Length - 1; column >= 0; column--)
        {
            TerminalCell cell = line.Cells[column];
            if (column == cursorColumn ||
                cell.IsContinuation ||
                cell.Text != " " ||
                cell.Style != TerminalStyle.Default ||
                cell.Hyperlink is not null)
            {
                return column + 1;
            }
        }

        return cursorColumn >= 0 ? cursorColumn + 1 : 0;
    }

    private static void FlushSegment(
        List<TerminalRenderSegmentSnapshot> segments,
        StringBuilder text,
        ResolvedStyle? style,
        ref int cellLength)
    {
        if (text.Length == 0 || style is null)
        {
            return;
        }

        segments.Add(new TerminalRenderSegmentSnapshot(
            text.ToString(),
            cellLength,
            style.Value.Foreground,
            style.Value.Background,
            style.Value.Bold,
            style.Value.Underline,
            style.Value.Hyperlink));
        text.Clear();
        cellLength = 0;
    }

    internal static void AppendSegment(InlineCollection inlines, TerminalRenderSegmentSnapshot segment)
    {
        var run = new Run(segment.Text);
        if (segment.Hyperlink is not null &&
            Uri.TryCreate(segment.Hyperlink, UriKind.Absolute, out Uri? navigateUri))
        {
            var hyperlink = new Hyperlink(run)
            {
                NavigateUri = navigateUri,
                Foreground = GetBrush(segment.Foreground),
                Background = GetBrush(segment.Background),
                FontWeight = segment.Bold ? FontWeights.SemiBold : FontWeights.Regular
            };

            if (segment.Underline)
            {
                hyperlink.TextDecorations = TextDecorations.Underline;
            }

            inlines.Add(hyperlink);
            return;
        }

        run.Foreground = GetBrush(segment.Foreground);
        run.Background = GetBrush(segment.Background);
        run.FontWeight = segment.Bold ? FontWeights.SemiBold : FontWeights.Regular;

        if (segment.Underline)
        {
            run.TextDecorations = TextDecorations.Underline;
        }

        inlines.Add(run);
    }

    private static void FlushRun(InlineCollection inlines, StringBuilder text, ResolvedStyle? style)
    {
        if (text.Length == 0 || style is null)
        {
            return;
        }

        var run = new Run(text.ToString());
        if (style.Value.Hyperlink is not null &&
            Uri.TryCreate(style.Value.Hyperlink, UriKind.Absolute, out Uri? navigateUri))
        {
            var hyperlink = new Hyperlink(run)
            {
                NavigateUri = navigateUri,
                Foreground = GetBrush(style.Value.Foreground),
                Background = GetBrush(style.Value.Background),
                FontWeight = style.Value.Bold ? FontWeights.SemiBold : FontWeights.Regular
            };

            if (style.Value.Underline)
            {
                hyperlink.TextDecorations = TextDecorations.Underline;
            }

            inlines.Add(hyperlink);
            text.Clear();
            return;
        }

        run.Foreground = GetBrush(style.Value.Foreground);
        run.Background = GetBrush(style.Value.Background);
        run.FontWeight = style.Value.Bold ? FontWeights.SemiBold : FontWeights.Regular;

        if (style.Value.Underline)
        {
            run.TextDecorations = TextDecorations.Underline;
        }

        inlines.Add(run);
        text.Clear();
    }

    internal static void InsertCursorAnchor(InlineCollection inlines, ref FrameworkElement? cursorAnchor)
    {
        if (cursorAnchor is not null)
        {
            return;
        }

        var anchor = new Border
        {
            Width = 0,
            Height = 0,
            Background = Brushes.Transparent,
            Focusable = false,
            IsHitTestVisible = false
        };

        var container = new InlineUIContainer(anchor)
        {
            BaselineAlignment = BaselineAlignment.TextBottom
        };

        inlines.Add(container);
        cursorAnchor = anchor;
    }

    private static ResolvedStyle ResolveStyle(TerminalStyle style, string? hyperlink, bool isCursor)
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

        return new ResolvedStyle(foreground, background, style.Bold, style.Underline, hyperlink);
    }

    private static TerminalCell CreateBlankCell(TerminalStyle style)
    {
        return new TerminalCell(" ", style, Hyperlink: null, IsContinuation: false, Width: 1);
    }

    private Rune MapActiveRune(Rune rune)
    {
        if (!rune.IsAscii || GetActiveCharacterSet() != TerminalCharacterSet.DecSpecialGraphics)
        {
            return rune;
        }

        return rune.Value switch
        {
            0x005F => new Rune(0x00A0),
            0x0060 => new Rune(0x25C6),
            0x0061 => new Rune(0x2592),
            0x0062 => new Rune(0x2409),
            0x0063 => new Rune(0x240C),
            0x0064 => new Rune(0x240D),
            0x0065 => new Rune(0x240A),
            0x0066 => new Rune(0x00B0),
            0x0067 => new Rune(0x00B1),
            0x0068 => new Rune(0x2424),
            0x0069 => new Rune(0x240B),
            0x006A => new Rune(0x2518),
            0x006B => new Rune(0x2510),
            0x006C => new Rune(0x250C),
            0x006D => new Rune(0x2514),
            0x006E => new Rune(0x253C),
            0x006F => new Rune(0x23BA),
            0x0070 => new Rune(0x23BB),
            0x0071 => new Rune(0x2500),
            0x0072 => new Rune(0x23BC),
            0x0073 => new Rune(0x23BD),
            0x0074 => new Rune(0x251C),
            0x0075 => new Rune(0x2524),
            0x0076 => new Rune(0x2534),
            0x0077 => new Rune(0x252C),
            0x0078 => new Rune(0x2502),
            0x0079 => new Rune(0x2264),
            0x007A => new Rune(0x2265),
            0x007B => new Rune(0x03C0),
            0x007C => new Rune(0x2260),
            0x007D => new Rune(0x00A3),
            0x007E => new Rune(0x00B7),
            _ => rune
        };
    }

    private TerminalCharacterSet GetActiveCharacterSet()
    {
        return _useG1CharacterSet ? _g1CharacterSet : _g0CharacterSet;
    }

    private static bool IsControlRune(Rune rune)
    {
        return rune.Value < 0x20 || rune.Value == 0x7F;
    }

    private static int GetDisplayWidth(Rune rune)
    {
        UnicodeCategory category = Rune.GetUnicodeCategory(rune);
        if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format)
        {
            return 0;
        }

        if (IsZeroWidthExtension(rune))
        {
            return 0;
        }

        if (rune.IsAscii)
        {
            return 1;
        }

        int value = rune.Value;
        if (value is
            >= 0x1100 and <= 0x115F or
            >= 0x2329 and <= 0x232A or
            >= 0x2E80 and <= 0xA4CF or
            >= 0xAC00 and <= 0xD7A3 or
            >= 0xF900 and <= 0xFAFF or
            >= 0xFE10 and <= 0xFE19 or
            >= 0xFE30 and <= 0xFE6F or
            >= 0xFF00 and <= 0xFF60 or
            >= 0xFFE0 and <= 0xFFE6 or
            >= 0x1F1E6 and <= 0x1F1FF or
            >= 0x1F300 and <= 0x1FAFF or
            >= 0x20000 and <= 0x3FFFD)
        {
            return 2;
        }

        return 1;
    }

    private static bool IsZeroWidthExtension(Rune rune)
    {
        int value = rune.Value;
        return value is
            0x200D or
            0xFE0E or
            0xFE0F or
            >= 0x1F3FB and <= 0x1F3FF or
            >= 0xE0100 and <= 0xE01EF;
    }

    private static bool IsZeroWidthJoiner(Rune rune)
    {
        return rune.Value == 0x200D;
    }

    private static bool IsRegionalIndicator(Rune rune)
    {
        return rune.Value is >= 0x1F1E6 and <= 0x1F1FF;
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
        OscEscape,
        Charset
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
        TerminalStyle SavedStyle,
        string? CurrentHyperlink,
        string? SavedHyperlink);

    private readonly record struct TerminalCell(
        string Text,
        TerminalStyle Style,
        string? Hyperlink,
        bool IsContinuation,
        int Width);

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
        bool Underline,
        string? Hyperlink);

    internal readonly record struct TerminalRenderSnapshot(
        TerminalRenderLineSnapshot[] Lines);

    internal readonly record struct TerminalRenderLineSnapshot(
        int AnchorSegmentIndex,
        int CellLength,
        TerminalRenderSegmentSnapshot[] Segments)
    {
        public bool ContentEquals(TerminalRenderLineSnapshot other)
        {
            if (AnchorSegmentIndex != other.AnchorSegmentIndex ||
                CellLength != other.CellLength ||
                Segments.Length != other.Segments.Length)
            {
                return false;
            }

            for (int index = 0; index < Segments.Length; index++)
            {
                if (Segments[index] != other.Segments[index])
                {
                    return false;
                }
            }

            return true;
        }
    }

    internal readonly record struct TerminalRenderSegmentSnapshot(
        string Text,
        int CellLength,
        Color Foreground,
        Color Background,
        bool Bold,
        bool Underline,
        string? Hyperlink);

    internal readonly record struct TerminalDocumentSnapshot(
        FlowDocument Document,
        FrameworkElement? CursorAnchor);
}
