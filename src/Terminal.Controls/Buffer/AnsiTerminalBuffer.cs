using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Globalization;

using Terminal.Unicode;
using Terminal.Settings;

namespace Terminal.Buffer;

internal enum ShellCommandZoneType
{
    PromptStart,
    CommandStart,
    CommandExecuted,
    CommandDone
}

internal sealed class ShellCommandZoneEventArgs(ShellCommandZoneType zoneType, int absoluteLine, int? exitCode) : EventArgs
{
    public ShellCommandZoneType ZoneType { get; } = zoneType;
    public int AbsoluteLine { get; } = absoluteLine;
    public int? ExitCode { get; } = exitCode;
}

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

internal enum UnderlineStyle
{
    None,
    Single,
    Double,
    Curly,
    Dotted,
    Dashed
}

internal sealed class AnsiTerminalBuffer
{
    private const int MinColumns = 20;
    private const int MinRows = 10;
    private const int DefaultScrollbackLimit = 2000;

    private static readonly Dictionary<Color, SolidColorBrush> BrushCache = [];
    private static readonly char[] CsiIntermediateCharacters = [' ', '!', '"', '#', '$', '%', '&', '\'', '(', ')', '*', '+', ',', '-', '.', '/'];

    private Color _defaultForeground = TerminalColorTheme.Default.Foreground;
    private Color _defaultBackground = TerminalColorTheme.Default.Background;
    private Color _cursorAccent = TerminalColorTheme.Default.Cursor;
    private readonly Color[] _defaultAnsiPalette = TerminalColorTheme.Default.AnsiPalette.ToArray();
    private readonly Color[] _ansiPalette = TerminalColorTheme.Default.AnsiPalette.ToArray();
    private readonly int _scrollbackLimit;
    private readonly List<TerminalLine> _scrollback = [];
    private readonly List<TerminalRenderLineSnapshot> _scrollbackRenderCache = [];
    private readonly StringBuilder _csiBuffer = new();
    private readonly StringBuilder _oscBuffer = new();
    private readonly StringBuilder _dcsBuffer = new();
    private readonly StringBuilder _pendingClusterText = new();
    private readonly Dictionary<int, bool> _savedPrivateModes = [];

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
    private int _leftMargin;
    private int _rightMargin;
    private bool _leftRightMarginEnabled;
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
    private int _modifyOtherKeys; // 0 = off, 1 = level1, 2 = level2
    private bool _synchronizedUpdateActive;
    private bool _synchronizedUpdateEndedDuringProcess;
    private bool _syntheticAlternateScreenActive;
    private bool _useG1CharacterSet;
    private bool _savedUseG1CharacterSet;
    private bool _savedInsertMode;
    private bool _savedOriginMode;
    private bool _savedAutoWrapEnabled = true;
    private bool _useUtf8MouseEncoding;
    private bool _useSgrMouseEncoding;
    private bool _useUrxvtMouseEncoding;
    private bool _mousePixelMode;
    private bool _screenReverse;
    private TerminalMouseTrackingMode _mouseTrackingMode;
    private TerminalCursorShape _cursorShape = TerminalCursorShape.Block;
    private TerminalCharacterSet _g0CharacterSet = TerminalCharacterSet.Ascii;
    private TerminalCharacterSet _g1CharacterSet = TerminalCharacterSet.Ascii;
    private TerminalCharacterSet _savedG0CharacterSet = TerminalCharacterSet.Ascii;
    private TerminalCharacterSet _savedG1CharacterSet = TerminalCharacterSet.Ascii;
    private string? _currentHyperlink;
    private string? _savedHyperlink;
    private string _windowTitle = string.Empty;
    private ScreenState? _pendingSyntheticAlternateScreenBackup;
    private string _lastPrintedClusterText = string.Empty;
    private int _lastPrintedClusterWidth;
    private int _pendingClusterWidth;
    private bool _pendingClusterJoinNext;
    private int _pendingClusterRegionalIndicatorCount;
    private bool _renderCacheDirty = true;
    private bool _screenRenderCacheDirty = true;
    private bool _cachedRenderShowCursor;
    private int _cachedVisibleScreenRow = -1;
    private int _kittyKeyboardFlags;
    private readonly Stack<int> _kittyKeyboardStack = new();

    public event EventHandler<string>? InputSequenceGenerated;
    public event EventHandler<string>? ClipboardSetRequested;
    public event EventHandler<string>? ClipboardQueryRequested;
    public event EventHandler<string>? CurrentDirectoryChanged;
    public event EventHandler<string>? NotificationRequested;
    public event EventHandler<ShellCommandZoneEventArgs>? ShellCommandZoneReceived;

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
    public int ModifyOtherKeysLevel => _modifyOtherKeys;
    private bool _ambiguousWidthIsWide;
    public bool AmbiguousWidthIsWide
    {
        get => _ambiguousWidthIsWide;
        set
        {
            if (_ambiguousWidthIsWide == value) return;
            _ambiguousWidthIsWide = value;
            InvalidateScreenRenderCache();
        }
    }
    public int CursorRow => _cursorRow;
    public int CursorColumn => Math.Clamp(_cursorColumn, 0, _columns - 1);
    public bool CursorBlinkEnabled => _cursorBlinkEnabled;
    public TerminalCursorShape CursorShape => _cursorShape;
    public bool CursorVisible => _cursorVisible;
    public bool FocusReportingEnabled => _focusReportingEnabled;
    public bool IsAlternateScreenActive => _primaryScreenBackup is not null;
    public bool SynchronizedUpdateActive => _synchronizedUpdateActive;
    public TerminalMouseEncoding MouseEncoding => ResolveMouseEncoding();
    public TerminalMouseTrackingMode MouseTrackingMode => _mouseTrackingMode;
    public bool MousePixelMode => _mousePixelMode;
    public int ScrollbackLineCount => _scrollback.Count;
    public int VisibleLineCount => GetLastRenderedScreenRow(showCursor: false) + 1;

    public TerminalColorTheme ColorTheme { get; private set; } = TerminalColorTheme.Default;

    public void ApplyColorTheme(TerminalColorTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);

        ColorTheme = theme;
        _defaultForeground = theme.Foreground;
        _defaultBackground = theme.Background;
        _cursorAccent = theme.Cursor;
        CopyPalette(theme.AnsiPalette, _defaultAnsiPalette);
        CopyPalette(theme.AnsiPalette, _ansiPalette);
        RebuildScrollbackRenderCache();
        InvalidateScreenRenderCache();
        _renderCacheDirty = true;
    }
    public int KittyKeyboardFlags => _kittyKeyboardFlags;

    public void Resize(short columns, short rows)
    {
        int newColumns = Math.Max(columns, (short)MinColumns);
        int newRows = Math.Max(rows, (short)MinRows);

        if (newColumns == _columns && newRows == _rows)
        {
            return;
        }

        bool[] resizedTabStops = CreateDefaultTabStops(newColumns);
        Array.Copy(_tabStops, resizedTabStops, Math.Min(_tabStops.Length, resizedTabStops.Length));
        _tabStops = resizedTabStops;

        if (_primaryScreenBackup is null)
        {
            ResizePrimaryScreen(newColumns, newRows);
        }
        else
        {
            ResizeActiveScreen(newColumns, newRows);
        }

        _columns = newColumns;
        _rows = newRows;
        _savedCursorRow = Math.Clamp(_savedCursorRow, 0, _rows - 1);
        _savedCursorColumn = Math.Clamp(_savedCursorColumn, 0, _columns - 1);
        ResetMargins();
        ResetScreenRenderCache();
    }

    private void ResizePrimaryScreen(int newColumns, int newRows)
    {
        bool preserveBottomRows = newRows < _rows;
        _screen = ResizeScreenBuffer(_screen, _rows, _columns, newRows, newColumns, preserveBottomRows);
        _cursorRow = AdjustRowForResize(_cursorRow, _rows, newRows, preserveBottomRows);
        _cursorColumn = Math.Clamp(_cursorColumn, 0, newColumns - 1);
        _savedCursorRow = AdjustRowForResize(_savedCursorRow, _rows, newRows, preserveBottomRows);
    }

    private void ResizeActiveScreen(int newColumns, int newRows)
    {
        _screen = ResizeScreenBuffer(_screen, _rows, _columns, newRows, newColumns, preserveBottomRows: false);
        _cursorRow = AdjustRowForResize(_cursorRow, _rows, newRows, preserveBottomRows: false);
        _cursorColumn = Math.Clamp(_cursorColumn, 0, newColumns - 1);
    }

    private static List<TerminalLine> ResizeScreenBuffer(
        List<TerminalLine> sourceScreen,
        int sourceRows,
        int sourceColumns,
        int targetRows,
        int targetColumns,
        bool preserveBottomRows)
    {
        var resizedScreen = CreateScreen(targetRows, targetColumns, TerminalStyle.Default);
        int copyRows = Math.Min(sourceRows, targetRows);
        int copyColumns = Math.Min(sourceColumns, targetColumns);
        int sourceStartRow = preserveBottomRows && targetRows < sourceRows
            ? sourceRows - targetRows
            : 0;
        int targetStartRow = preserveBottomRows && targetRows > sourceRows
            ? targetRows - sourceRows
            : 0;

        for (int row = 0; row < copyRows; row++)
        {
            TerminalLine source = sourceScreen[sourceStartRow + row];
            TerminalLine target = resizedScreen[targetStartRow + row];
            Array.Copy(source.Cells, 0, target.Cells, 0, copyColumns);
            SanitizeRightEdge(source, target, copyColumns, sourceColumns);
        }

        return resizedScreen;
    }

    private static int AdjustRowForResize(int row, int sourceRows, int targetRows, bool preserveBottomRows)
    {
        int sourceStartRow = preserveBottomRows && targetRows < sourceRows
            ? sourceRows - targetRows
            : 0;
        int targetStartRow = preserveBottomRows && targetRows > sourceRows
            ? targetRows - sourceRows
            : 0;
        return Math.Clamp(row - sourceStartRow + targetStartRow, 0, targetRows - 1);
    }

    private static void SanitizeRightEdge(TerminalLine source, TerminalLine target, int copiedColumns, int sourceColumns)
    {
        if (copiedColumns <= 0 || copiedColumns >= sourceColumns)
        {
            return;
        }

        if (source.Cells[copiedColumns].IsContinuation)
        {
            int lastCopiedColumn = copiedColumns - 1;
            target.Cells[lastCopiedColumn] = CreateBlankCell(TerminalStyle.Default);
        }
    }

    public bool Process(string text)
    {
        _synchronizedUpdateEndedDuringProcess = false;
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
        return _synchronizedUpdateEndedDuringProcess;
    }

    public bool ForceEndSynchronizedUpdate()
    {
        if (!_synchronizedUpdateActive)
        {
            return false;
        }

        _synchronizedUpdateActive = false;
        _synchronizedUpdateEndedDuringProcess = false;
        return true;
    }

    public bool ForceExitAlternateScreen()
    {
        if (_primaryScreenBackup is null)
        {
            return false;
        }

        ExitAlternateScreen();
        return true;
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

        int lastScreenRow = GetLastRenderedScreenRow(showCursor);
        if (_cachedVisibleScreenRow != lastScreenRow)
        {
            _cachedVisibleScreenRow = lastScreenRow;
            _renderCacheDirty = true;
        }

        int visibleScreenLineCount = lastScreenRow + 1;
        bool includeScrollback = _primaryScreenBackup is null;
        int renderScrollbackCount = includeScrollback ? _scrollbackRenderCache.Count : 0;
        int totalLineCount = renderScrollbackCount + visibleScreenLineCount;
        if (_renderCacheDirty || _combinedRenderCache.Length != totalLineCount)
        {
            _combinedRenderCache = new TerminalRenderLineSnapshot[totalLineCount];
            if (renderScrollbackCount > 0)
            {
                _scrollbackRenderCache.CopyTo(_combinedRenderCache, 0);
            }

            if (visibleScreenLineCount > 0)
            {
                Array.Copy(
                    _screenRenderCache,
                    0,
                    _combinedRenderCache,
                    renderScrollbackCount,
                    visibleScreenLineCount);
            }

            _renderCacheDirty = false;
        }

        return new TerminalRenderSnapshot(_combinedRenderCache, _ambiguousWidthIsWide);
    }

    public TerminalDocumentSnapshot CreateDocument(FontFamily fontFamily, double fontSize, bool showCursor)
    {
        var document = new FlowDocument
        {
            FontFamily = fontFamily,
            FontSize = fontSize,
            Background = GetBrush(_defaultBackground),
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

    /// <summary>
    /// Extracts ANSI-free plain text for a half-open range of absolute line indices,
    /// where index 0..ScrollbackLineCount-1 map to scrollback rows and subsequent
    /// indices map to the active screen rows. Indices are clamped to the valid range,
    /// so callers may pass <see cref="int.MaxValue"/> to capture through the last line.
    /// Absolute line indices are stable as output scrolls into the scrollback, which is
    /// why they can be recorded earlier (e.g. at an OSC 133 marker) and resolved later.
    /// </summary>
    internal string GetPlainTextForAbsoluteLineRange(int startInclusive, int endExclusive)
    {
        int scrollbackCount = _scrollback.Count;
        // Mirror CreatePlainTextSnapshot: ignore the trailing blank screen rows so an
        // unbounded end (int.MaxValue) does not pad the result with empty lines.
        int totalLines = scrollbackCount + FindLastVisibleScreenRow(showCursor: false) + 1;
        int start = Math.Clamp(startInclusive, 0, totalLines);
        int end = Math.Clamp(endExclusive, start, totalLines);

        var builder = new StringBuilder();
        bool isFirstLine = true;
        for (int absolute = start; absolute < end; absolute++)
        {
            TerminalLine line = absolute < scrollbackCount
                ? _scrollback[absolute]
                : _screen[absolute - scrollbackCount];
            AppendPlainTextLine(builder, line, ref isFirstLine);
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

    private void RebuildScrollbackRenderCache()
    {
        _scrollbackRenderCache.Clear();
        foreach (TerminalLine line in _scrollback)
        {
            _scrollbackRenderCache.Add(CreateLineSnapshot(line, -1, -1, showCursor: false));
        }
    }

    private void ResetMargins()
    {
        _scrollTop = 0;
        _scrollBottom = _rows - 1;
        _leftMargin = 0;
        _rightMargin = _columns - 1;
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
        _modifyOtherKeys = 0;
        _useG1CharacterSet = false;
        _savedUseG1CharacterSet = false;
        _savedInsertMode = false;
        _savedOriginMode = false;
        _savedAutoWrapEnabled = true;
        _useUtf8MouseEncoding = false;
        _useSgrMouseEncoding = false;
        _useUrxvtMouseEncoding = false;
        _mousePixelMode = false;
        _screenReverse = false;
        _mouseTrackingMode = TerminalMouseTrackingMode.Off;
        _synchronizedUpdateActive = false;
        _leftRightMarginEnabled = false;
        _syntheticAlternateScreenActive = false;
        _savedPrivateModes.Clear();
        _g0CharacterSet = TerminalCharacterSet.Ascii;
        _g1CharacterSet = TerminalCharacterSet.Ascii;
        _savedG0CharacterSet = TerminalCharacterSet.Ascii;
        _savedG1CharacterSet = TerminalCharacterSet.Ascii;
        _currentHyperlink = null;
        _savedHyperlink = null;
        _pendingSyntheticAlternateScreenBackup = null;
        _windowTitle = string.Empty;
        _lastPrintedClusterText = string.Empty;
        _lastPrintedClusterWidth = 0;
        ClearPendingCluster();
        _state = ParserState.Normal;
        _csiBuffer.Clear();
        _oscBuffer.Clear();
        _dcsBuffer.Clear();
        Array.Copy(_defaultAnsiPalette, _ansiPalette, _defaultAnsiPalette.Length);
        _kittyKeyboardFlags = 0;
        _kittyKeyboardStack.Clear();
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
            case ParserState.DcsEntry:
                ProcessDcsEntry(ch);
                break;
            case ParserState.DcsParam:
                ProcessDcsParam(ch);
                break;
            case ParserState.DcsIntermediate:
                ProcessDcsIntermediate(ch);
                break;
            case ParserState.DcsPassthrough:
                ProcessDcsPassthrough(ch);
                break;
            case ParserState.DcsPassthroughEscape:
                ProcessDcsPassthroughEscape(ch);
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
            case '\u009b':
                _csiBuffer.Clear();
                _state = ParserState.Csi;
                return;
            case '\u009d':
                _oscBuffer.Clear();
                _state = ParserState.Osc;
                return;
            case '\u009f':
                _dcsBuffer.Clear();
                _state = ParserState.DcsEntry;
                return;
            case '\u009c':
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
            case 'P':
                _dcsBuffer.Clear();
                _state = ParserState.DcsEntry;
                return;
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

    private void ProcessDcsEntry(char ch)
    {
        if (ch == '')
        {
            DispatchDcs(_dcsBuffer.ToString());
            _state = ParserState.Normal;
            return;
        }

        if (ch == '')
        {
            _state = ParserState.DcsPassthroughEscape;
            return;
        }

        if (ch >= 0x20 && ch <= 0x2F)
        {
            _dcsBuffer.Append(ch);
            _state = ParserState.DcsIntermediate;
            return;
        }

        if (ch >= 0x30 && ch <= 0x3F)
        {
            _dcsBuffer.Append(ch);
            _state = ParserState.DcsParam;
            return;
        }

        if (ch >= 0x40 && ch <= 0x7E)
        {
            _dcsBuffer.Append(ch);
            _state = ParserState.DcsPassthrough;
            return;
        }
    }

    private void ProcessDcsParam(char ch)
    {
        if (ch == '')
        {
            DispatchDcs(_dcsBuffer.ToString());
            _state = ParserState.Normal;
            return;
        }

        if (ch == '')
        {
            _state = ParserState.DcsPassthroughEscape;
            return;
        }

        if (ch >= 0x30 && ch <= 0x3F)
        {
            _dcsBuffer.Append(ch);
            return;
        }

        if (ch >= 0x20 && ch <= 0x2F)
        {
            _dcsBuffer.Append(ch);
            _state = ParserState.DcsIntermediate;
            return;
        }

        if (ch >= 0x40 && ch <= 0x7E)
        {
            _dcsBuffer.Append(ch);
            _state = ParserState.DcsPassthrough;
            return;
        }
    }

    private void ProcessDcsIntermediate(char ch)
    {
        if (ch == '')
        {
            DispatchDcs(_dcsBuffer.ToString());
            _state = ParserState.Normal;
            return;
        }

        if (ch == '')
        {
            _state = ParserState.DcsPassthroughEscape;
            return;
        }

        if (ch >= 0x20 && ch <= 0x2F)
        {
            _dcsBuffer.Append(ch);
            return;
        }

        if (ch >= 0x40 && ch <= 0x7E)
        {
            _dcsBuffer.Append(ch);
            _state = ParserState.DcsPassthrough;
            return;
        }
    }

    private void ProcessDcsPassthrough(char ch)
    {
        if (ch == '')
        {
            DispatchDcs(_dcsBuffer.ToString());
            _state = ParserState.Normal;
            return;
        }

        if (ch == '')
        {
            _state = ParserState.DcsPassthroughEscape;
            return;
        }

        _dcsBuffer.Append(ch);
    }

    private void ProcessDcsPassthroughEscape(char ch)
    {
        if (ch == '\\')
        {
            DispatchDcs(_dcsBuffer.ToString());
            _state = ParserState.Normal;
            return;
        }

        _dcsBuffer.Append('');
        _dcsBuffer.Append(ch);
        _state = ParserState.DcsPassthrough;
    }

    private void DispatchDcs(string content)
    {
        // DECRQSS: ESC P $ q <Pt> ST  (buffer contains "$q<Pt>")
        if (content.StartsWith("$q", StringComparison.Ordinal))
        {
            string pt = content[2..];
            switch (pt)
            {
                case "m":
                    EmitInputSequence("P1$r0m\\");
                    break;
                case "r":
                    EmitInputSequence($"P1$r1;{_scrollBottom + 1}r\\");
                    break;
                default:
                    EmitInputSequence("P0$r\\");
                    break;
            }
        }
        // All other DCS sequences (Sixel, DECUDK, etc.) are silently ignored.
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
            string previousTitle = _windowTitle;
            _windowTitle = value;
            UpdateSyntheticAlternateScreenFromTitle(previousTitle, value);
            return;
        }

        if (command == "8")
        {
            DispatchOscHyperlink(value);
            return;
        }

        if (command is "10" or "11" or "12" && value == "?")
        {
            Color queryColor = command switch
            {
                "10" => _defaultForeground,
                "11" => _defaultBackground,
                _ => _cursorAccent
            };
            EmitInputSequence($"]{command};{FormatRgbColor(queryColor)}");
            return;
        }

        if (command == "9")
        {
            if (!string.IsNullOrEmpty(value))
            {
                NotificationRequested?.Invoke(this, value);
            }

            return;
        }

        if (command == "7")
        {
            if (!string.IsNullOrEmpty(value))
            {
                string localPath = value;
                if (Uri.TryCreate(value, UriKind.Absolute, out Uri? dirUri) && dirUri.IsFile)
                {
                    // Uri.LocalPath converts file://localhost/C:/foo to \\localhost\C:\foo (UNC) on Windows.
                    // Decode AbsolutePath directly and strip the leading slash before a Windows drive letter.
                    string decoded = Uri.UnescapeDataString(dirUri.AbsolutePath);
                    localPath = decoded.Length >= 3 && decoded[0] == '/' && char.IsLetter(decoded[1]) && decoded[2] == ':'
                        ? decoded[1..]
                        : decoded;
                }

                CurrentDirectoryChanged?.Invoke(this, localPath);
            }

            return;
        }

        if (command == "4")
        {
            DispatchOscPaletteChange(value);
            return;
        }

        if (command is "133" or "633")
        {
            DispatchOscShellIntegration(value);
            return;
        }

        if (command == "52")
        {
            DispatchOscClipboard(value);
        }
    }

    private void DispatchOscPaletteChange(string value)
    {
        string[] parts = value.Split(';');
        for (int i = 0; i + 1 < parts.Length; i += 2)
        {
            if (!int.TryParse(parts[i], out int paletteIndex) || paletteIndex < 0 || paletteIndex >= _ansiPalette.Length)
            {
                continue;
            }

            string colorSpec = parts[i + 1];
            if (colorSpec == "?")
            {
                EmitInputSequence($"]4;{paletteIndex};{FormatRgbColor(_ansiPalette[paletteIndex])}");
            }
            else if (TryParseOscColorSpec(colorSpec, out Color color))
            {
                _ansiPalette[paletteIndex] = color;
                InvalidateScreenRenderCache();
            }
        }
    }

    private void DispatchOscShellIntegration(string value)
    {
        int separatorIndex = value.IndexOf(';');
        string type = separatorIndex >= 0 ? value[..separatorIndex] : value;
        string parameters = separatorIndex >= 0 ? value[(separatorIndex + 1)..] : string.Empty;

        ShellCommandZoneType? zoneType = type switch
        {
            "A" => ShellCommandZoneType.PromptStart,
            "B" => ShellCommandZoneType.CommandStart,
            "C" => ShellCommandZoneType.CommandExecuted,
            "D" => ShellCommandZoneType.CommandDone,
            _ => null
        };

        if (zoneType is null)
        {
            return;
        }

        int? exitCode = null;
        if (zoneType == ShellCommandZoneType.CommandDone)
        {
            int semicolonInParams = parameters.IndexOf(';');
            string exitCodeStr = semicolonInParams >= 0 ? parameters[..semicolonInParams] : parameters;
            if (int.TryParse(exitCodeStr, out int code))
            {
                exitCode = code;
            }
        }

        int absoluteLine = _scrollback.Count + _cursorRow;
        ShellCommandZoneReceived?.Invoke(this, new ShellCommandZoneEventArgs(zoneType.Value, absoluteLine, exitCode));
    }

    private static bool TryParseOscColorSpec(string spec, out Color color)
    {
        color = default;
        if (spec.StartsWith("rgb:", StringComparison.OrdinalIgnoreCase))
        {
            string[] components = spec[4..].Split('/');
            if (components.Length != 3)
            {
                return false;
            }

            if (!TryParseHexColorComponent(components[0], out byte r)) return false;
            if (!TryParseHexColorComponent(components[1], out byte g)) return false;
            if (!TryParseHexColorComponent(components[2], out byte b)) return false;
            color = Color.FromRgb(r, g, b);
            return true;
        }

        if (spec.StartsWith('#') && spec.Length >= 7)
        {
            if (!byte.TryParse(spec.AsSpan(1, 2), System.Globalization.NumberStyles.HexNumber, null, out byte r)) return false;
            if (!byte.TryParse(spec.AsSpan(3, 2), System.Globalization.NumberStyles.HexNumber, null, out byte g)) return false;
            if (!byte.TryParse(spec.AsSpan(5, 2), System.Globalization.NumberStyles.HexNumber, null, out byte b)) return false;
            color = Color.FromRgb(r, g, b);
            return true;
        }

        return false;
    }

    private static bool TryParseHexColorComponent(string hex, out byte value)
    {
        int len = Math.Min(2, hex.Length);
        if (len == 0)
        {
            value = 0;
            return false;
        }

        return byte.TryParse(hex.AsSpan(0, len), System.Globalization.NumberStyles.HexNumber, null, out value);
    }

    private static string FormatRgbColor(Color color)
    {
        return $"rgb:{color.R:x2}{color.R:x2}/{color.G:x2}{color.G:x2}/{color.B:x2}{color.B:x2}";
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
                _cursorColumn = Math.Min(_leftRightMarginEnabled ? _rightMargin : _columns - 1, _cursorColumn + GetParameter(parameters, 0, 1));
                break;
            case 'D':
                _cursorColumn = Math.Max(_leftRightMarginEnabled ? _leftMargin : 0, _cursorColumn - GetParameter(parameters, 0, 1));
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
            {
                int colParam = GetParameter(parameters, 0, 1) - 1;
                int minCol = _leftRightMarginEnabled ? _leftMargin : 0;
                int maxCol = _leftRightMarginEnabled ? _rightMargin : _columns - 1;
                _cursorColumn = Math.Clamp(colParam, minCol, maxCol);
                break;
            }
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
                if (isSecondary && GetParameter(parameters, 0, -1) == 4)
                {
                    _modifyOtherKeys = GetParameter(parameters, 1, 0);
                }
                else if (!isSecondary)
                {
                    ApplySgr(ParseSgrParameters(paramText));
                }

                break;
            case 'n':
                DispatchDeviceStatusReport(parameters, isPrivate);
                break;
            case 'p':
                if (intermediate == "!")
                {
                    SoftResetTerminal();
                }
                else if (intermediate == "$" && isPrivate)
                {
                    ReportPrivateModeState(GetParameter(parameters, 0, 0));
                }

                break;
            case 'q':
                if (intermediate == " ")
                {
                    SetCursorStyle(GetParameter(parameters, 0, 0));
                }
                else if (isSecondary && GetParameter(parameters, 0, 0) == 0)
                {
                    EmitInputSequence("\u001bP>|ConPtyTerminal 1.0\u001b\\");
                }

                break;
            case 'r':
                if (!isPrivate)
                {
                    SetScrollRegion(parameters);
                }
                else
                {
                    RestorePrivateModes(parameters);
                }

                break;
            case 's':
                if (!isPrivate)
                {
                    if (_leftRightMarginEnabled)
                    {
                        SetLeftRightMargins(parameters);
                    }
                    else
                    {
                        SaveCursorState();
                    }
                }
                else
                {
                    SavePrivateModes(parameters);
                }

                break;
            case 't':
                DispatchWindowOperation(GetParameter(parameters, 0, 0));
                break;
            case 'u':
                if (isSecondary)
                {
                    KittyPushFlags(GetParameter(parameters, 0, 0));
                }
                else if (isPrivate)
                {
                    KittyQueryFlags();
                }
                else if (rawParams.Length > 0 && rawParams[0] == '<')
                {
                    KittyPopFlags(GetParameter(ParseParameters(rawParams[1..]), 0, 1));
                }
                else if (rawParams.Length > 0 && rawParams[0] == '=')
                {
                    string modeParams = rawParams[1..];
                    int?[] mp = ParseParameters(modeParams);
                    KittySetFlags(GetParameter(mp, 0, 0), GetParameter(mp, 1, 1));
                }
                else
                {
                    RestoreCursorState();
                }

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
                case 5:
                    _screenReverse = enabled;
                    InvalidateScreenRenderCache();
                    break;
                case 6:
                    if (_originMode != enabled)
                    {
                        _originMode = enabled;
                        MoveCursorHome();
                    }
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
                    if (enabled) _mouseTrackingMode = TerminalMouseTrackingMode.X10;
                    else if (_mouseTrackingMode == TerminalMouseTrackingMode.X10) _mouseTrackingMode = TerminalMouseTrackingMode.Off;
                    break;
                case 1002:
                    if (enabled) _mouseTrackingMode = TerminalMouseTrackingMode.ButtonEvent;
                    else if (_mouseTrackingMode == TerminalMouseTrackingMode.ButtonEvent) _mouseTrackingMode = TerminalMouseTrackingMode.Off;
                    break;
                case 1003:
                    if (enabled) _mouseTrackingMode = TerminalMouseTrackingMode.AnyEvent;
                    else _mouseTrackingMode = TerminalMouseTrackingMode.Off;
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
                case 1016:
                    _mousePixelMode = enabled;
                    if (enabled)
                    {
                        _useSgrMouseEncoding = true;
                    }
                    break;
                case 66:
                    _applicationKeypad = enabled;
                    break;
                case 2004:
                    _bracketedPasteEnabled = enabled;
                    break;
                case 2026:
                    if (_synchronizedUpdateActive && !enabled)
                    {
                        _synchronizedUpdateEndedDuringProcess = true;
                    }

                    _synchronizedUpdateActive = enabled;
                    break;
                case 3:
                    SetDecColm(enabled);
                    break;
                case 69:
                    _leftRightMarginEnabled = enabled;
                    if (!enabled)
                    {
                        _leftMargin = 0;
                        _rightMargin = _columns - 1;
                    }

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

    private void ReportPrivateModeState(int mode)
    {
        int state = mode switch
        {
            1 => _applicationCursorKeys ? 1 : 2,
            3 => _columns == 132 ? 1 : 2,
            5 => _screenReverse ? 1 : 2,
            6 => _originMode ? 1 : 2,
            7 => _autoWrapEnabled ? 1 : 2,
            12 => _cursorBlinkEnabled ? 1 : 2,
            25 => _cursorVisible ? 1 : 2,
            47 or 1047 => _primaryScreenBackup is not null && !_syntheticAlternateScreenActive ? 1 : 2,
            66 => _applicationKeypad ? 1 : 2,
            1000 => _mouseTrackingMode == TerminalMouseTrackingMode.X10 ? 1 : 2,
            1002 => _mouseTrackingMode == TerminalMouseTrackingMode.ButtonEvent ? 1 : 2,
            1003 => _mouseTrackingMode == TerminalMouseTrackingMode.AnyEvent ? 1 : 2,
            1004 => _focusReportingEnabled ? 1 : 2,
            1005 => _useUtf8MouseEncoding ? 1 : 2,
            1006 => _useSgrMouseEncoding ? 1 : 2,
            1007 => _alternateScrollEnabled ? 1 : 2,
            1015 => _useUrxvtMouseEncoding ? 1 : 2,
            1016 => _mousePixelMode ? 1 : 2,
            1049 => _primaryScreenBackup is not null && !_syntheticAlternateScreenActive ? 1 : 2,
            2004 => _bracketedPasteEnabled ? 1 : 2,
            2026 => _synchronizedUpdateActive ? 1 : 2,
            69 => _leftRightMarginEnabled ? 1 : 2,
            _ => 0
        };
        EmitInputSequence($"[?{mode};{state}$y");
    }

    private void DispatchWindowOperation(int operation)
    {
        switch (operation)
        {
            case 18:
                EmitInputSequence($"[8;{_rows};{_columns}t");
                break;
            case 20:
                EmitInputSequence($"]L{_windowTitle}\\");
                break;
            case 21:
                EmitInputSequence($"]l{_windowTitle}\\");
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
        _modifyOtherKeys = 0;
        _useG1CharacterSet = false;
        _useUtf8MouseEncoding = false;
        _useSgrMouseEncoding = false;
        _useUrxvtMouseEncoding = false;
        _mousePixelMode = false;
        _screenReverse = false;
        _mouseTrackingMode = TerminalMouseTrackingMode.Off;
        _synchronizedUpdateActive = false;
        _leftRightMarginEnabled = false;
        _pendingSyntheticAlternateScreenBackup = null;
        _g0CharacterSet = TerminalCharacterSet.Ascii;
        _g1CharacterSet = TerminalCharacterSet.Ascii;
        _currentHyperlink = null;
        _kittyKeyboardFlags = 0;
        _kittyKeyboardStack.Clear();
        _savedPrivateModes.Clear();
        ResetMargins();
        InvalidateScreenRenderCache();
    }

    private void KittyPushFlags(int flags)
    {
        _kittyKeyboardStack.Push(_kittyKeyboardFlags);
        _kittyKeyboardFlags = flags & 0x1F;
    }

    private void KittyPopFlags(int count)
    {
        int popCount = Math.Max(1, count);
        for (int i = 0; i < popCount; i++)
        {
            if (_kittyKeyboardStack.Count > 0)
            {
                _kittyKeyboardFlags = _kittyKeyboardStack.Pop();
            }
            else
            {
                _kittyKeyboardFlags = 0;
            }
        }
    }

    private void KittySetFlags(int flags, int mode)
    {
        int masked = flags & 0x1F;
        _kittyKeyboardFlags = mode switch
        {
            1 => masked,
            2 => _kittyKeyboardFlags | masked,
            3 => _kittyKeyboardFlags & ~masked,
            _ => masked
        };
    }

    private void KittyQueryFlags()
    {
        EmitInputSequence($"[?{_kittyKeyboardFlags}u");
    }

    private bool GetPrivateModeEnabled(int mode)
    {
        return mode switch
        {
            1 => _applicationCursorKeys,
            3 => _columns == 132,
            5 => _screenReverse,
            6 => _originMode,
            7 => _autoWrapEnabled,
            12 => _cursorBlinkEnabled,
            25 => _cursorVisible,
            47 or 1047 => _primaryScreenBackup is not null && !_syntheticAlternateScreenActive,
            66 => _applicationKeypad,
            1000 => _mouseTrackingMode == TerminalMouseTrackingMode.X10,
            1002 => _mouseTrackingMode == TerminalMouseTrackingMode.ButtonEvent,
            1003 => _mouseTrackingMode == TerminalMouseTrackingMode.AnyEvent,
            1004 => _focusReportingEnabled,
            1005 => _useUtf8MouseEncoding,
            1006 => _useSgrMouseEncoding,
            1007 => _alternateScrollEnabled,
            1015 => _useUrxvtMouseEncoding,
            1016 => _mousePixelMode,
            1048 or 1049 => _primaryScreenBackup is not null && !_syntheticAlternateScreenActive,
            2004 => _bracketedPasteEnabled,
            2026 => _synchronizedUpdateActive,
            69 => _leftRightMarginEnabled,
            _ => false
        };
    }

    private void SavePrivateModes(int?[] parameters)
    {
        foreach (int? parameter in parameters)
        {
            // Modes 47/1047/1048/1049 involve alternate-screen transitions that carry buffer
            // contents and cursor state — they cannot be round-tripped through a simple bool,
            // so they are intentionally excluded from generic save/restore.
            if (parameter.HasValue && parameter.Value is not (47 or 1047 or 1048 or 1049))
            {
                _savedPrivateModes[parameter.Value] = GetPrivateModeEnabled(parameter.Value);
            }
        }
    }

    private void RestorePrivateModes(int?[] parameters)
    {
        foreach (int? parameter in parameters)
        {
            if (parameter.HasValue && parameter.Value is not (47 or 1047 or 1048 or 1049) &&
                _savedPrivateModes.TryGetValue(parameter.Value, out bool saved))
            {
                SetPrivateMode([parameter.Value], saved);
            }
        }
    }

    private void EnterAlternateScreen()
    {
        if (_primaryScreenBackup is not null)
        {
            return;
        }

        _pendingSyntheticAlternateScreenBackup = null;
        _syntheticAlternateScreenActive = false;
        _primaryScreenBackup = CaptureScreenState();

        _screen = CreateScreen(_rows, _columns, TerminalStyle.Default);
        _cursorRow = 0;
        _cursorColumn = 0;
        _savedCursorRow = 0;
        _savedCursorColumn = 0;
        _currentStyle = TerminalStyle.Default;
        _savedStyle = TerminalStyle.Default;
        _currentHyperlink = null;
        _savedHyperlink = null;
        _modifyOtherKeys = 0;
        ResetMargins();
    }

    private void ExitAlternateScreen()
    {
        if (_primaryScreenBackup is null)
        {
            return;
        }

        int targetRows = _rows;
        int targetColumns = _columns;
        ScreenState backup = _primaryScreenBackup;

        _screen = CloneScreen(backup.Screen);
        _tabStops = (bool[])backup.TabStops.Clone();
        _rows = backup.Rows;
        _columns = backup.Columns;
        _cursorRow = backup.CursorRow;
        _cursorColumn = backup.CursorColumn;
        _savedCursorRow = backup.SavedCursorRow;
        _savedCursorColumn = backup.SavedCursorColumn;
        _scrollTop = backup.ScrollTop;
        _scrollBottom = backup.ScrollBottom;
        _currentStyle = backup.Style;
        _savedStyle = backup.SavedStyle;
        _currentHyperlink = backup.CurrentHyperlink;
        _savedHyperlink = backup.SavedHyperlink;
        _modifyOtherKeys = backup.ModifyOtherKeys;
        _kittyKeyboardFlags = backup.KittyKeyboardFlags;
        _kittyKeyboardStack.Clear();
        for (int i = backup.KittyStack.Count - 1; i >= 0; i--)
        {
            _kittyKeyboardStack.Push(backup.KittyStack[i]);
        }
        _primaryScreenBackup = null;

        if (_rows != targetRows || _columns != targetColumns)
        {
            Resize((short)targetColumns, (short)targetRows);
        }

        InvalidateScreenRenderCache();
        _syntheticAlternateScreenActive = false;
    }

    private void CaptureSyntheticAlternateScreenCandidate()
    {
        if (_primaryScreenBackup is not null)
        {
            return;
        }

        _pendingSyntheticAlternateScreenBackup = CaptureScreenState();
    }

    private ScreenState CaptureScreenState()
    {
        return new ScreenState(
            CloneScreen(_screen),
            (bool[])_tabStops.Clone(),
            _rows,
            _columns,
            _cursorRow,
            _cursorColumn,
            _savedCursorRow,
            _savedCursorColumn,
            _scrollTop,
            _scrollBottom,
            _currentStyle,
            _savedStyle,
            _currentHyperlink,
            _savedHyperlink,
            _modifyOtherKeys,
            _kittyKeyboardFlags,
            [.. _kittyKeyboardStack]);
    }

    private void UpdateSyntheticAlternateScreenFromTitle(string previousTitle, string nextTitle)
    {
        bool nextTitleIsClaude = IsClaudeSyntheticAlternateScreenTitle(nextTitle);
        if (_syntheticAlternateScreenActive)
        {
            if (IsClaudeSyntheticAlternateScreenTitle(previousTitle) && !nextTitleIsClaude)
            {
                ExitAlternateScreen();
            }

            return;
        }

        if (!nextTitleIsClaude)
        {
            _pendingSyntheticAlternateScreenBackup = null;
            return;
        }

        if (_primaryScreenBackup is not null)
        {
            return;
        }

        _primaryScreenBackup = _pendingSyntheticAlternateScreenBackup ?? CaptureScreenState();
        _pendingSyntheticAlternateScreenBackup = null;
        _syntheticAlternateScreenActive = true;
        InvalidateScreenRenderCache();
    }

    private static bool IsClaudeSyntheticAlternateScreenTitle(string title)
    {
        return title.Equals("claude", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Claude Code", StringComparison.OrdinalIgnoreCase);
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

    private void ApplySgr(SgrParam[] tokens)
    {
        if (tokens.Length == 0)
        {
            _currentStyle = TerminalStyle.Default;
            return;
        }

        for (int index = 0; index < tokens.Length; index++)
        {
            int code = tokens[index].Code;
            switch (code)
            {
                case 0:
                    _currentStyle = TerminalStyle.Default;
                    break;
                case 1:
                    _currentStyle = _currentStyle with { Bold = true };
                    break;
                case 2:
                    _currentStyle = _currentStyle with { Dim = true };
                    break;
                case 3:
                    _currentStyle = _currentStyle with { Italic = true };
                    break;
                case 4:
                    _currentStyle = _currentStyle with { UnderlineStyle = ResolveUnderlineStyle(tokens[index]) };
                    break;
                case 5:
                case 6:
                    _currentStyle = _currentStyle with { Blink = true };
                    break;
                case 7:
                    _currentStyle = _currentStyle with { Inverse = true };
                    break;
                case 8:
                    _currentStyle = _currentStyle with { Invisible = true };
                    break;
                case 9:
                    _currentStyle = _currentStyle with { Strikethrough = true };
                    break;
                case 22:
                    _currentStyle = _currentStyle with { Bold = false, Dim = false };
                    break;
                case 23:
                    _currentStyle = _currentStyle with { Italic = false };
                    break;
                case 24:
                    _currentStyle = _currentStyle with { UnderlineStyle = UnderlineStyle.None };
                    break;
                case 25:
                    _currentStyle = _currentStyle with { Blink = false };
                    break;
                case 27:
                    _currentStyle = _currentStyle with { Inverse = false };
                    break;
                case 28:
                    _currentStyle = _currentStyle with { Invisible = false };
                    break;
                case 29:
                    _currentStyle = _currentStyle with { Strikethrough = false };
                    break;
                case 53:
                    _currentStyle = _currentStyle with { Overline = true };
                    break;
                case 55:
                    _currentStyle = _currentStyle with { Overline = false };
                    break;
                case 59:
                    _currentStyle = _currentStyle with { UnderlineColor = null };
                    break;
                case >= 30 and <= 37:
                    _currentStyle = _currentStyle with { Foreground = _ansiPalette[code - 30] };
                    break;
                case 39:
                    _currentStyle = _currentStyle with { Foreground = null };
                    break;
                case >= 40 and <= 47:
                    _currentStyle = _currentStyle with { Background = _ansiPalette[code - 40] };
                    break;
                case 49:
                    _currentStyle = _currentStyle with { Background = null };
                    break;
                case >= 90 and <= 97:
                    _currentStyle = _currentStyle with { Foreground = _ansiPalette[8 + (code - 90)] };
                    break;
                case >= 100 and <= 107:
                    _currentStyle = _currentStyle with { Background = _ansiPalette[8 + (code - 100)] };
                    break;
                case 38:
                case 48:
                case 58:
                    if (TryReadExtendedColor(tokens, ref index, out Color color))
                    {
                        if (code == 38)
                        {
                            _currentStyle = _currentStyle with { Foreground = color };
                        }
                        else if (code == 48)
                        {
                            _currentStyle = _currentStyle with { Background = color };
                        }
                        else
                        {
                            _currentStyle = _currentStyle with { UnderlineColor = color };
                        }
                    }

                    break;
            }
        }
    }

    private static UnderlineStyle ResolveUnderlineStyle(SgrParam token)
    {
        if (token.Sub is null || token.Sub.Length == 0)
        {
            return UnderlineStyle.Single;
        }

        return token.Sub[0] switch
        {
            0 => UnderlineStyle.None,
            2 => UnderlineStyle.Double,
            3 => UnderlineStyle.Curly,
            4 => UnderlineStyle.Dotted,
            5 => UnderlineStyle.Dashed,
            _ => UnderlineStyle.Single
        };
    }

    private bool TryReadExtendedColor(SgrParam[] tokens, ref int index, out Color color)
    {
        color = default;
        SgrParam current = tokens[index];

        if (current.Sub is { Length: >= 1 })
        {
            int mode = current.Sub[0];
            if (mode == 5 && current.Sub.Length >= 2)
            {
                color = ResolveXtermColor(current.Sub[1]);
                return true;
            }

            if (mode == 2 && current.Sub.Length >= 4)
            {
                color = Color.FromRgb(
                    (byte)Math.Clamp(current.Sub[1], 0, 255),
                    (byte)Math.Clamp(current.Sub[2], 0, 255),
                    (byte)Math.Clamp(current.Sub[3], 0, 255));
                return true;
            }

            return false;
        }

        if (index + 1 >= tokens.Length)
        {
            return false;
        }

        int legacyMode = tokens[index + 1].Code;
        if (legacyMode == 5 && index + 2 < tokens.Length)
        {
            color = ResolveXtermColor(tokens[index + 2].Code);
            index += 2;
            return true;
        }

        if (legacyMode == 2 && index + 4 < tokens.Length)
        {
            color = Color.FromRgb(
                (byte)Math.Clamp(tokens[index + 2].Code, 0, 255),
                (byte)Math.Clamp(tokens[index + 3].Code, 0, 255),
                (byte)Math.Clamp(tokens[index + 4].Code, 0, 255));
            index += 4;
            return true;
        }

        return false;
    }

    private Color ResolveXtermColor(int index)
    {
        if (index < 0)
        {
            return _defaultForeground;
        }

        if (index < _ansiPalette.Length)
        {
            return _ansiPalette[index];
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

        return _defaultForeground;
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

    private void SetDecColm(bool enable132)
    {
        int targetColumns = enable132 ? 132 : 80;
        _columns = Math.Max(targetColumns, MinColumns);
        _screen = ResizeScreenBuffer(_screen, _rows, _screen[0].Cells.Length, _rows, _columns, preserveBottomRows: false);
        _tabStops = CreateDefaultTabStops(_columns);
        _cursorRow = 0;
        _cursorColumn = 0;
        ResetMargins();
        for (int row = 0; row < _rows; row++)
        {
            ClearEntireLine(row);
        }

        ResetScreenRenderCache();
    }

    private void SetLeftRightMargins(int?[] parameters)
    {
        int left = Math.Clamp(GetParameter(parameters, 0, 1) - 1, 0, _columns - 1);
        int right = Math.Clamp(GetParameter(parameters, 1, _columns) - 1, 0, _columns - 1);
        if (right <= left)
        {
            _leftMargin = 0;
            _rightMargin = _columns - 1;
        }
        else
        {
            _leftMargin = left;
            _rightMargin = right;
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
        int rightLimit = _leftRightMarginEnabled ? _rightMargin + 1 : _columns;
        int insertCount = Math.Min(Math.Max(count, 1), rightLimit - _cursorColumn);
        TerminalCell[] cells = _screen[_cursorRow].Cells;
        for (int column = rightLimit - 1; column >= _cursorColumn + insertCount; column--)
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
        int rightLimit = _leftRightMarginEnabled ? _rightMargin + 1 : _columns;
        int deleteCount = Math.Min(Math.Max(count, 1), rightLimit - _cursorColumn);
        TerminalCell[] cells = _screen[_cursorRow].Cells;
        for (int column = _cursorColumn; column < rightLimit - deleteCount; column++)
        {
            cells[column] = cells[column + deleteCount];
        }

        for (int column = rightLimit - deleteCount; column < rightLimit; column++)
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
                CaptureSyntheticAlternateScreenCandidate();
                for (int row = 0; row < _rows; row++)
                {
                    ClearEntireLine(row);
                }

                break;
            case 3:
                CaptureSyntheticAlternateScreenCandidate();
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
        int rightLimit = _leftRightMarginEnabled ? _rightMargin + 1 : _columns;
        int leftLimit = _leftRightMarginEnabled ? _leftMargin : 0;
        switch (mode)
        {
            case 0:
                FillRange(_screen[_cursorRow], _cursorColumn, rightLimit);
                break;
            case 1:
                FillRange(_screen[_cursorRow], leftLimit, _cursorColumn + 1);
                break;
            case 2:
                FillRange(_screen[_cursorRow], leftLimit, rightLimit);
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
        TerminalLine targetLine = _screen[_cursorRow];
        int targetColumn = _cursorColumn > 0 ? _cursorColumn - 1 : FindLastOccupiedColumn(targetLine);

        if (targetColumn < 0)
        {
            // Cursor is at column 0 with no previous character on this line.
            // After an autowrap the base character sits at the end of the previous row.
            if (_cursorRow > 0)
            {
                targetLine = _screen[_cursorRow - 1];
                targetColumn = FindLastOccupiedColumn(targetLine);
            }

            if (targetColumn < 0)
            {
                return;
            }
        }

        while (targetColumn > 0 && targetLine.Cells[targetColumn].IsContinuation)
        {
            targetColumn--;
        }

        TerminalCell cell = targetLine.Cells[targetColumn];
        targetLine.Cells[targetColumn] = cell with { Text = cell.Text + rune.ToString() };
        _lastPrintedClusterText = targetLine.Cells[targetColumn].Text;
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

    private int FindPreviousOccupiedColumn() => FindLastOccupiedColumn(_screen[_cursorRow]);

    private static int FindLastOccupiedColumn(TerminalLine line)
    {
        for (int column = line.Cells.Length - 1; column >= 0; column--)
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
        _cursorColumn = _originMode && _leftRightMarginEnabled ? _leftMargin : 0;
    }

    private void SetCursorPosition(int rowParameter, int columnParameter)
    {
        int rowOffset = Math.Max(rowParameter, 1) - 1;
        int baseRow = _originMode ? _scrollTop : 0;
        int maxRow = _originMode ? _scrollBottom : _rows - 1;
        _cursorRow = Math.Clamp(baseRow + rowOffset, baseRow, maxRow);
        int colBase = _originMode && _leftRightMarginEnabled ? _leftMargin : 0;
        int colMax = _originMode && _leftRightMarginEnabled ? _rightMargin : _columns - 1;
        _cursorColumn = Math.Clamp(colBase + Math.Max(columnParameter, 1) - 1, colBase, colMax);
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

    private int GetLastRenderedScreenRow(bool showCursor)
    {
        return _primaryScreenBackup is not null
            ? _rows - 1
            : FindLastVisibleScreenRow(showCursor);
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
            ResolvedStyle style = ResolveStyle(cell.Style, cell.Hyperlink, isCursor, _screenReverse);
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

    private void AppendLineSnapshot(InlineCollection inlines, TerminalRenderLineSnapshot lineSnapshot, ref bool isFirstLine, ref FrameworkElement? cursorAnchor)
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
            ResolvedStyle style = ResolveStyle(cell.Style, cell.Hyperlink, isCursor, _screenReverse);
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
            style.Value.Italic,
            style.Value.UnderlineStyle,
            style.Value.UnderlineColor,
            style.Value.Strikethrough,
            style.Value.Overline,
            style.Value.Hyperlink));
        text.Clear();
        cellLength = 0;
    }

    internal static void AppendSegment(InlineCollection inlines, TerminalRenderSegmentSnapshot segment)
    {
        var run = new Run(segment.Text);
        run.FontWeight = segment.Bold ? FontWeights.SemiBold : FontWeights.Regular;
        if (segment.Italic) run.FontStyle = FontStyles.Italic;

        if (segment.Hyperlink is not null &&
            Uri.TryCreate(segment.Hyperlink, UriKind.Absolute, out Uri? navigateUri))
        {
            var hyperlink = new Hyperlink(run)
            {
                NavigateUri = navigateUri,
                Foreground = GetBrush(segment.Foreground),
                Background = GetBrush(segment.Background),
            };
            ApplyTextDecorations(hyperlink, segment.UnderlineStyle, segment.UnderlineColor, segment.Strikethrough, segment.Overline);
            inlines.Add(hyperlink);
            return;
        }

        ApplyTextDecorations(run, segment.UnderlineStyle, segment.UnderlineColor, segment.Strikethrough, segment.Overline);
        run.Foreground = GetBrush(segment.Foreground);
        run.Background = GetBrush(segment.Background);
        inlines.Add(run);
    }

    private static void ApplyTextDecorations(Inline element, UnderlineStyle underlineStyle, Color? underlineColor, bool strikethrough, bool overline)
    {
        if (underlineStyle == UnderlineStyle.None && !strikethrough && !overline)
        {
            element.TextDecorations = null;
            return;
        }

        var combined = new TextDecorationCollection();
        if (underlineStyle != UnderlineStyle.None)
        {
            AddUnderlineDecorations(combined, underlineStyle, underlineColor, foreground: null);
        }

        if (strikethrough) foreach (TextDecoration d in TextDecorations.Strikethrough) combined.Add(d);
        if (overline) foreach (TextDecoration d in TextDecorations.OverLine) combined.Add(d);
        element.TextDecorations = combined;
    }

    internal static void AddUnderlineDecorations(TextDecorationCollection decorations, UnderlineStyle style, Color? underlineColor, Color? foreground)
    {
        Brush? brush = underlineColor.HasValue
            ? GetBrush(underlineColor.Value)
            : foreground.HasValue ? GetBrush(foreground.Value) : null;

        if (style == UnderlineStyle.Double)
        {
            Pen? doublePen = brush is null ? null : new Pen(brush, 1);
            decorations.Add(new TextDecoration(TextDecorationLocation.Underline, doublePen, 0, TextDecorationUnit.FontRecommended, TextDecorationUnit.FontRecommended));
            decorations.Add(new TextDecoration(TextDecorationLocation.Underline, doublePen, 2, TextDecorationUnit.Pixel, TextDecorationUnit.FontRecommended));
            return;
        }

        DashStyle? dashStyle = style switch
        {
            UnderlineStyle.Dotted => DashStyles.Dot,
            UnderlineStyle.Dashed => DashStyles.Dash,
            _ => null
        };

        if (brush is null && dashStyle is null)
        {
            foreach (TextDecoration d in TextDecorations.Underline) decorations.Add(d);
            return;
        }

        var pen = new Pen(brush ?? Brushes.Transparent, 1);
        if (dashStyle is not null) pen.DashStyle = dashStyle;
        decorations.Add(new TextDecoration(TextDecorationLocation.Underline, pen, 0, TextDecorationUnit.FontRecommended, TextDecorationUnit.FontRecommended));
    }

    private static void FlushRun(InlineCollection inlines, StringBuilder text, ResolvedStyle? style)
    {
        if (text.Length == 0 || style is null)
        {
            return;
        }

        var run = new Run(text.ToString());
        run.FontWeight = style.Value.Bold ? FontWeights.SemiBold : FontWeights.Regular;
        if (style.Value.Italic) run.FontStyle = FontStyles.Italic;

        if (style.Value.Hyperlink is not null &&
            Uri.TryCreate(style.Value.Hyperlink, UriKind.Absolute, out Uri? navigateUri))
        {
            var hyperlink = new Hyperlink(run)
            {
                NavigateUri = navigateUri,
                Foreground = GetBrush(style.Value.Foreground),
                Background = GetBrush(style.Value.Background),
            };
            ApplyTextDecorations(hyperlink, style.Value.UnderlineStyle, style.Value.UnderlineColor, style.Value.Strikethrough, style.Value.Overline);
            inlines.Add(hyperlink);
            text.Clear();
            return;
        }

        ApplyTextDecorations(run, style.Value.UnderlineStyle, style.Value.UnderlineColor, style.Value.Strikethrough, style.Value.Overline);
        run.Foreground = GetBrush(style.Value.Foreground);
        run.Background = GetBrush(style.Value.Background);
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

    private ResolvedStyle ResolveStyle(TerminalStyle style, string? hyperlink, bool isCursor, bool screenReverse = false)
    {
        Color foreground = style.Foreground ?? _defaultForeground;
        Color background = style.Background ?? _defaultBackground;

        if (style.Inverse)
        {
            (foreground, background) = (background, foreground);
        }

        if (style.Dim)
        {
            foreground = DimColor(foreground);
        }

        if (style.Invisible && !isCursor)
        {
            foreground = background;
        }

        if (isCursor)
        {
            (foreground, background) = (background, foreground);
            if (foreground == background)
            {
                background = _cursorAccent;
                foreground = _defaultBackground;
            }
        }

        // DECSCNM is a screen-level transform applied after all per-cell attribute resolution.
        if (screenReverse)
        {
            (foreground, background) = (background, foreground);
        }

        return new ResolvedStyle(foreground, background, style.Bold, style.Italic, style.UnderlineStyle, style.UnderlineColor, style.Strikethrough, style.Overline, hyperlink);
    }

    private static Color DimColor(Color color) =>
        Color.FromRgb(
            (byte)Math.Round(color.R * 0.55),
            (byte)Math.Round(color.G * 0.55),
            (byte)Math.Round(color.B * 0.55));

    private static void CopyPalette(IReadOnlyList<Color> source, Color[] destination)
    {
        for (int index = 0; index < destination.Length; index++)
        {
            destination[index] = source[index];
        }
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
        return rune.Value < 0x20 || rune.Value == 0x7F || rune.Value is >= 0x80 and <= 0x9F;
    }

    private int GetDisplayWidth(Rune rune) =>
        UnicodeWidth.GetWidth(rune, _ambiguousWidthIsWide);

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

    private static SgrParam[] ParseSgrParameters(string paramText)
    {
        if (string.IsNullOrEmpty(paramText))
        {
            return [];
        }

        string[] tokens = paramText.Split(';');
        var result = new SgrParam[tokens.Length];
        for (int i = 0; i < tokens.Length; i++)
        {
            string token = tokens[i];
            int colon = token.IndexOf(':');
            if (colon >= 0)
            {
                int code = int.TryParse(token.AsSpan(0, colon), out int c) ? c : 0;
                string[] subParts = token[(colon + 1)..].Split(':');
                var nonEmpty = new List<int>(subParts.Length);
                foreach (string part in subParts)
                {
                    if (part.Length > 0)
                    {
                        nonEmpty.Add(int.TryParse(part, out int s) ? s : 0);
                    }
                }

                result[i] = nonEmpty.Count > 0 ? new SgrParam(code, nonEmpty.ToArray()) : new SgrParam(code);
            }
            else
            {
                result[i] = new SgrParam(int.TryParse(token, out int code) ? code : 0);
            }
        }

        return result;
    }

    private readonly struct SgrParam(int code, int[]? sub = null)
    {
        public readonly int Code = code;
        public readonly int[]? Sub = sub;
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
        Charset,
        DcsEntry,
        DcsParam,
        DcsIntermediate,
        DcsPassthrough,
        DcsPassthroughEscape
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
        bool[] TabStops,
        int Rows,
        int Columns,
        int CursorRow,
        int CursorColumn,
        int SavedCursorRow,
        int SavedCursorColumn,
        int ScrollTop,
        int ScrollBottom,
        TerminalStyle Style,
        TerminalStyle SavedStyle,
        string? CurrentHyperlink,
        string? SavedHyperlink,
        int ModifyOtherKeys,
        int KittyKeyboardFlags,
        List<int> KittyStack);

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
        bool Dim,
        bool Italic,
        UnderlineStyle UnderlineStyle,
        Color? UnderlineColor,
        bool Blink,
        bool Inverse,
        bool Invisible,
        bool Strikethrough,
        bool Overline)
    {
        public static readonly TerminalStyle Default = new(null, null, false, false, false, UnderlineStyle.None, null, false, false, false, false, false);
    }

    private readonly record struct ResolvedStyle(
        Color Foreground,
        Color Background,
        bool Bold,
        bool Italic,
        UnderlineStyle UnderlineStyle,
        Color? UnderlineColor,
        bool Strikethrough,
        bool Overline,
        string? Hyperlink);

    internal readonly record struct TerminalRenderSnapshot(
        TerminalRenderLineSnapshot[] Lines,
        bool AmbiguousWidthIsWide = false);

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
        bool Italic,
        UnderlineStyle UnderlineStyle,
        Color? UnderlineColor,
        bool Strikethrough,
        bool Overline,
        string? Hyperlink);

    internal readonly record struct TerminalDocumentSnapshot(
        FlowDocument Document,
        FrameworkElement? CursorAnchor);
}
