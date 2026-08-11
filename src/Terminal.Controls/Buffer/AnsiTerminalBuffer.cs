using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Terminal.Unicode;
using Terminal.Settings;
using Terminal.Rendering;

using SimdVector = System.Numerics.Vector;

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

/// <summary>
/// ConEmu 由来の OSC 9;4 タスクバー進捗シーケンスのパース結果。
/// <see cref="State"/> は 0=解除 / 1=通常 / 2=エラー / 3=不確定 / 4=一時停止(警告)、
/// <see cref="Progress"/> は 0–100 にクランプ済み。
/// </summary>
internal sealed class TaskbarProgressEventArgs(int state, int progress) : EventArgs
{
    public int State { get; } = state;
    public int Progress { get; } = progress;
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
    private const int DefaultScrollbackLimit = 10000;

    private static readonly Dictionary<Color, SolidColorBrush> BrushCache = [];
    private Color _defaultForeground = TerminalColorTheme.Default.Foreground;
    private Color _defaultBackground = TerminalColorTheme.Default.Background;
    private Color _cursorAccent = TerminalColorTheme.Default.Cursor;
    // Theme-provided reset targets for OSC 110/111/112 (reset dynamic fg/bg/cursor to the configured default).
    private Color _themeForeground = TerminalColorTheme.Default.Foreground;
    private Color _themeBackground = TerminalColorTheme.Default.Background;
    private Color _themeCursor = TerminalColorTheme.Default.Cursor;
    private readonly Color[] _defaultAnsiPalette = TerminalColorTheme.Default.AnsiPalette.ToArray();
    private readonly Color[] _ansiPalette = TerminalColorTheme.Default.AnsiPalette.ToArray();
    private readonly TerminalScreenStore _screenStore;
    private readonly List<TerminalRenderLineSnapshot> _scrollbackRenderCache = [];
    private readonly VtParser _parser;
    private readonly StringBuilder _pendingClusterText = new();
    private readonly Dictionary<int, bool> _savedPrivateModes = [];

    private List<TerminalLine> _screen => _screenStore.Screen;

    private List<TerminalLine> _scrollback => _screenStore.Scrollback;
    private TerminalRenderLineSnapshot[] _screenRenderCache;
    private TerminalRenderLineSnapshot[] _combinedRenderCache = [];
    private bool[] _tabStops;
    private ScreenState? _primaryScreenBackup;
    private int _columns;
    private int _rows;
    private int _cursorRow;
    private int _cursorColumn;
    // DEC autowrap is a pending state, not an extra grid column.
    private bool _wrapPending;
    private int _savedCursorRow;
    private int _savedCursorColumn;
    private bool _savedWrapPending;
    private int _scrollTop;
    private int _scrollBottom;
    private int _leftMargin;
    private int _rightMargin;
    private bool _leftRightMarginEnabled;
    private TerminalStyle _currentStyle = TerminalStyle.Default;
    private TerminalStyle _savedStyle = TerminalStyle.Default;
    private bool _cursorVisible = true;
    private bool _cursorBlinkEnabled = true;
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
    private int _glLevel; // GL invocation: 0=G0, 1=G1, 2=G2, 3=G3 (SI / SO / LS2 / LS3)
    private int _savedGlLevel;
    private int _singleShift = -1; // -1 = none, 2 = SS2 (G2), 3 = SS3 (G3); applies to next graphic char only
    private bool _lineFeedNewlineMode; // LNM (ANSI mode 20): LF / VT / FF also perform a carriage return
    private bool _altSendsEscape = true; // DEC private modes 1036 / 1039: Meta/Alt prefixes an ESC on key input
    private bool _eightBitInput; // DEC private mode 1034: interpret Meta (8-bit input). Tracked for DECRQM; no functional change on Windows
    private bool _autoRepeatKeys = true; // DEC private mode 8 (DECARM): keyboard auto-repeat. Tracked for DECRQM; repeat is OS-driven
    private bool _reverseWraparound; // DEC private mode 45: backspace past the left margin wraps to the previous line
    private string _answerbackString = string.Empty; // Reply to ENQ (0x05); empty by default
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
    private TerminalCharacterSet _g2CharacterSet = TerminalCharacterSet.Ascii;
    private TerminalCharacterSet _g3CharacterSet = TerminalCharacterSet.Ascii;
    private TerminalCharacterSet _savedG2CharacterSet = TerminalCharacterSet.Ascii;
    private TerminalCharacterSet _savedG3CharacterSet = TerminalCharacterSet.Ascii;
    private string? _currentHyperlink;
    private string? _savedHyperlink;
    private string _windowTitle = string.Empty;
    // XTWINOPS 22/23 window-title stack (vim / tmux save & restore the title around their session).
    private readonly Stack<string> _windowTitleStack = new();
    private ScreenState? _pendingSyntheticAlternateScreenBackup;
    private string _lastPrintedClusterText = string.Empty;
    private int _lastPrintedClusterWidth;
    private int _pendingClusterWidth;
    private bool _pendingClusterJoinNext;
    private int _pendingClusterRegionalIndicatorCount;
    private bool _renderCacheDirty = true;
    private bool _scrollbackCombinedCacheDirty = true;
    private bool _screenRenderCacheDirty = true;
    private bool _cachedRenderShowCursor;
    private int _cachedVisibleScreenRow = -1;
    private int _kittyKeyboardFlags;
    private readonly Stack<int> _kittyKeyboardStack = new();
    private readonly Dictionary<string, string> _termcapOverrides = new(StringComparer.Ordinal);
    private int _unknownCsiSequenceCount;
    private int _unknownDcsSequenceCount;

    public event EventHandler<string>? InputSequenceGenerated;
    public event EventHandler<string>? ClipboardSetRequested;
    public event EventHandler<string>? ClipboardQueryRequested;
    public event EventHandler<string>? CurrentDirectoryChanged;
    public event EventHandler<string>? NotificationRequested;

    /// <summary>
    /// 通常状態で BEL（0x07）を受信したときに発火する。OSC / DCS の終端文字として
    /// 消費される 0x07 は対象外で、あくまで制御文字として届いたベルのみを通知する。
    /// </summary>
    public event EventHandler? BellReceived;

    /// <summary>
    /// ConEmu OSC 9;4（タスクバー進捗）を受信したときに発火する。デスクトップ通知
    /// （OSC 9;&lt;message&gt;）とは区別され、<c>9;4;</c> プレフィックスのときだけこちらが発火する。
    /// </summary>
    public event EventHandler<TaskbarProgressEventArgs>? TaskbarProgressChanged;
    public event EventHandler<ShellCommandZoneEventArgs>? ShellCommandZoneReceived;

    /// <summary>
    /// Raised with the full command-line text the shell reported via the
    /// OSC 633;E shell-integration marker, just before the command executes.
    /// Used to build a navigable command history without scraping the screen.
    /// </summary>
    public event EventHandler<string>? ShellCommandLineReceived;

    /// <summary>
    /// Raised with the shell's PSReadLine history file path, reported via the
    /// OSC 633;P;HistoryPath shell-integration property. Lets the host seed the
    /// command history from the exact file the shell uses.
    /// </summary>
    public event EventHandler<string>? ShellHistoryPathReceived;

    public AnsiTerminalBuffer(short columns, short rows, int scrollbackLimit = DefaultScrollbackLimit)
    {
        int normalizedScrollbackLimit = Math.Max(scrollbackLimit, rows);
        _parser = new VtParser(
            ProcessControl,
            ProcessEscapeCommand,
            DecodeCsi,
            DispatchOsc,
            DispatchDcs,
            ProcessCharsetDesignation,
            ProcessDecLineSize);
        _columns = Math.Max(columns, (short)MinColumns);
        _rows = Math.Max(rows, (short)MinRows);
        _screenStore = new TerminalScreenStore(_rows, _columns, normalizedScrollbackLimit);
        _screenRenderCache = new TerminalRenderLineSnapshot[_rows];
        _tabStops = CreateDefaultTabStops(_columns);
        ResetMargins();
    }

    public string WindowTitle => _windowTitle;
    public bool ApplicationCursorKeysEnabled => _applicationCursorKeys;

    // DEC private modes 1036/1039: when disabled, Alt/Meta key input is sent without the ESC prefix.
    public bool AltSendsEscape => _altSendsEscape;
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
            if (_scrollback.Count > 0 || _screen.Any(static line => !IsLineBlank(line)))
            {
                ReflowForAmbiguousWidthChange(value);
            }

            _ambiguousWidthIsWide = value;
            InvalidateScreenRenderCache();
        }
    }
    public int CursorRow => _cursorRow;
    public int CursorColumn => Math.Clamp(_cursorColumn, 0, _columns - 1);
    internal bool WrapPendingForTests => _wrapPending;
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

    /// <summary>
    /// Number of CSI sequences whose final character is not implemented by this terminal.
    /// This is intentionally a counter rather than a logging side effect so callers can
    /// inspect parser compatibility without enabling diagnostics globally.
    /// </summary>
    public int UnknownCsiSequenceCount => _unknownCsiSequenceCount;

    /// <summary>
    /// Number of DCS sequences whose introducer/type is not implemented. Supported but
    /// intentionally ignored Sixel sequences are not included.
    /// </summary>
    public int UnknownDcsSequenceCount => _unknownDcsSequenceCount;

    public TerminalColorTheme ColorTheme { get; private set; } = TerminalColorTheme.Default;

    public void ApplyColorTheme(TerminalColorTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);

        ColorTheme = theme;
        _defaultForeground = theme.Foreground;
        _defaultBackground = theme.Background;
        _cursorAccent = theme.Cursor;
        _themeForeground = theme.Foreground;
        _themeBackground = theme.Background;
        _themeCursor = theme.Cursor;
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
        ReflowPrimaryScreen(newColumns, newRows);
    }

    private void ReflowPrimaryScreen(int newColumns, int newRows)
    {
        bool hadScrollback = _scrollback.Count > 0;
        bool cursorWrapPending = _wrapPending;
        bool savedCursorWrapPending = _savedWrapPending;
        var source = new List<TerminalLine>(_scrollback.Count + _screen.Count);
        source.AddRange(_scrollback);
        int screenLineCount = hadScrollback ? _screen.Count : Math.Max(_cursorRow + 1, FindLastVisibleScreenRow(showCursor: false) + 1);
        source.AddRange(_screen.Take(screenLineCount));

        int cursorSourceRow = _scrollback.Count + _cursorRow;
        int savedCursorSourceRow = _scrollback.Count + _savedCursorRow;
        List<TerminalLine> reflowed = TerminalReflowCalculator.ReflowLinesWithWrapState(
            source,
            newColumns,
            cursorSourceRow,
            _cursorColumn + (cursorWrapPending ? 1 : 0),
            out int cursorRow,
            out int cursorColumn,
            savedCursorSourceRow,
            _savedCursorColumn + (savedCursorWrapPending ? 1 : 0),
            out int savedCursorRow,
            out int savedCursorColumn,
            out bool mappedCursorWrapPending,
            out bool mappedSavedCursorWrapPending);

        int screenStart = Math.Max(0, reflowed.Count - newRows);
        int historyStart = Math.Max(0, screenStart - _screenStore.ScrollbackLimit);
        var history = new List<TerminalLine>(screenStart - historyStart);
        for (int row = historyStart; row < screenStart; row++)
        {
            history.Add(reflowed[row]);
        }

        List<TerminalLine> screen = CreateScreen(newRows, newColumns, TerminalStyle.Default);
        int copiedRows = reflowed.Count - screenStart;
        int targetStart = hadScrollback ? newRows - copiedRows : 0;
        for (int row = 0; row < copiedRows; row++)
        {
            screen[targetStart + row] = reflowed[screenStart + row];
        }

        _screenStore.ApplyReflow(screen, history);

        _cursorRow = Math.Clamp(targetStart + cursorRow - screenStart, 0, newRows - 1);
        _cursorColumn = Math.Clamp(cursorColumn, 0, newColumns - 1);
        _wrapPending = cursorWrapPending && _autoWrapEnabled && mappedCursorWrapPending;
        _savedCursorRow = Math.Clamp(targetStart + savedCursorRow - screenStart, 0, newRows - 1);
        _savedCursorColumn = Math.Clamp(savedCursorColumn, 0, newColumns - 1);
        _savedWrapPending = savedCursorWrapPending && _autoWrapEnabled && mappedSavedCursorWrapPending;
        RebuildScrollbackRenderCache();
    }


    private void ResizeActiveScreen(int newColumns, int newRows)
    {
        bool cursorWrapPending = _wrapPending;
        bool savedCursorWrapPending = _savedWrapPending;
        List<TerminalLine> reflowed = TerminalReflowCalculator.ReflowLinesWithWrapState(
            _screen,
            newColumns,
            _cursorRow,
            _cursorColumn + (cursorWrapPending ? 1 : 0),
            out int cursorRow,
            out int cursorColumn,
            _savedCursorRow,
            _savedCursorColumn + (savedCursorWrapPending ? 1 : 0),
            out int savedCursorRow,
            out int savedCursorColumn,
            out bool mappedCursorWrapPending,
            out bool mappedSavedCursorWrapPending);

        List<TerminalLine> screen = CreateScreen(newRows, newColumns, TerminalStyle.Default);
        int copyRows = Math.Min(newRows, reflowed.Count);
        for (int row = 0; row < copyRows; row++)
        {
            screen[row] = reflowed[row];
        }

        _screenStore.ReplaceScreen(screen);

        _cursorRow = Math.Clamp(cursorRow, 0, newRows - 1);
        _cursorColumn = Math.Clamp(cursorColumn, 0, newColumns - 1);
        _wrapPending = cursorWrapPending && _autoWrapEnabled && mappedCursorWrapPending;
        _savedCursorRow = Math.Clamp(savedCursorRow, 0, newRows - 1);
        _savedCursorColumn = Math.Clamp(savedCursorColumn, 0, newColumns - 1);
        _savedWrapPending = savedCursorWrapPending && _autoWrapEnabled && mappedSavedCursorWrapPending;
    }

    private void ReflowForAmbiguousWidthChange(bool ambiguousAsWide)
    {
        bool cursorWrapPending = _wrapPending;
        bool savedCursorWrapPending = _savedWrapPending;
        bool hadScrollback = _primaryScreenBackup is null && _scrollback.Count > 0;
        var source = new List<TerminalLine>(_scrollback.Count + _screen.Count);
        int screenLineCount = _primaryScreenBackup is null
            ? (hadScrollback ? _screen.Count : Math.Max(_cursorRow + 1, FindLastVisibleScreenRow(showCursor: false) + 1))
            : _screen.Count;
        if (_primaryScreenBackup is null)
        {
            source.AddRange(_scrollback);
        }

        source.AddRange(_screen.Take(screenLineCount));
        int cursorSourceRow = (_primaryScreenBackup is null ? _scrollback.Count : 0) + _cursorRow;
        int savedCursorSourceRow = (_primaryScreenBackup is null ? _scrollback.Count : 0) + _savedCursorRow;
        List<TerminalLine> reflowed = TerminalWidthReflowCalculator.ReflowLines(
            source,
            _columns,
            cursorSourceRow,
            _cursorColumn,
            cursorWrapPending,
            out int cursorRow,
            out int cursorColumn,
            out bool mappedCursorWrapPending,
            savedCursorSourceRow,
            _savedCursorColumn,
            savedCursorWrapPending,
            out int savedCursorRow,
            out int savedCursorColumn,
            out bool mappedSavedCursorWrapPending,
            ambiguousAsWide);

        if (_primaryScreenBackup is null)
        {
            int screenStart = Math.Max(0, reflowed.Count - _rows);
            int historyStart = Math.Max(0, screenStart - _screenStore.ScrollbackLimit);
            var history = new List<TerminalLine>(screenStart - historyStart);
            for (int row = historyStart; row < screenStart; row++)
            {
                history.Add(reflowed[row]);
            }

            var screen = CreateScreen(_rows, _columns, TerminalStyle.Default);
            int copiedRows = reflowed.Count - screenStart;
            int targetStart = hadScrollback ? _rows - copiedRows : 0;
            for (int row = 0; row < copiedRows; row++)
            {
                screen[targetStart + row] = reflowed[screenStart + row];
            }

            _screenStore.ApplyReflow(screen, history);
            _cursorRow = Math.Clamp(targetStart + cursorRow - screenStart, 0, _rows - 1);
            _savedCursorRow = Math.Clamp(targetStart + savedCursorRow - screenStart, 0, _rows - 1);
        }
        else
        {
            var screen = CreateScreen(_rows, _columns, TerminalStyle.Default);
            int copyRows = Math.Min(_rows, reflowed.Count);
            for (int row = 0; row < copyRows; row++)
            {
                screen[row] = reflowed[row];
            }

            _screenStore.ReplaceScreen(screen);
            _cursorRow = Math.Clamp(cursorRow, 0, _rows - 1);
            _savedCursorRow = Math.Clamp(savedCursorRow, 0, _rows - 1);
        }

        _cursorColumn = Math.Clamp(cursorColumn, 0, _columns - 1);
        _savedCursorColumn = Math.Clamp(savedCursorColumn, 0, _columns - 1);
        _wrapPending = cursorWrapPending && _autoWrapEnabled && mappedCursorWrapPending;
        _savedWrapPending = savedCursorWrapPending && _autoWrapEnabled && mappedSavedCursorWrapPending;
        RebuildScrollbackRenderCache();
        ResetScreenRenderCache();
    }


    public bool Process(string text)
    {
        _synchronizedUpdateEndedDuringProcess = false;
        for (int index = 0; index < text.Length;)
        {
            // Bulk fast path: when the parser is in its default state and nothing about the
            // current state can change how a printable ASCII character is placed, scan ahead with
            // SIMD for a run of such characters and write them in one batch. This skips the
            // per-character rune decode, width lookup, grapheme-cluster state machine, and per-cell
            // string allocation that dominate CPU when a program streams plain text (e.g. cat).
            if (_parser.IsNormal && CanUseAsciiFastPath())
            {
                int runLength = ScanPrintableAsciiRun(text.AsSpan(index));
                if (runLength > 0)
                {
                    WritePrintableAsciiRun(text.AsSpan(index, runLength));
                    index += runLength;
                    continue;
                }
            }

            if (_parser.IsNormal &&
                Rune.TryGetRuneAt(text, index, out Rune rune) &&
                !IsControlRune(rune))
            {
                ProcessRune(rune);
                index += rune.Utf16SequenceLength;
                continue;
            }

            FlushPendingCluster();
            _parser.Process(text[index]);
            index++;
        }

        FlushPendingCluster();
        InvalidateScreenRenderCache();
        return _synchronizedUpdateEndedDuringProcess;
    }

    private const char PrintableAsciiMin = ' ';
    private const char PrintableAsciiMax = '~';
    private static readonly string[] PrintableAsciiStrings = CreatePrintableAsciiStrings();

    private static string[] CreatePrintableAsciiStrings()
    {
        var table = new string[PrintableAsciiMax - PrintableAsciiMin + 1];
        for (int index = 0; index < table.Length; index++)
        {
            table[index] = ((char)(PrintableAsciiMin + index)).ToString();
        }

        return table;
    }

    // True when a printable ASCII character would be placed identically by the bulk fast path and
    // the per-rune path. The fast path assumes the character maps to itself (ASCII charset), starts
    // a fresh cell, and does not continue a grapheme cluster.
    // Test seam: forces the per-rune path so tests can assert the fast path and the per-rune path
    // place identical output for the same single Process call.
    internal bool AsciiFastPathDisabled { get; set; }

    private bool CanUseAsciiFastPath()
    {
        return !AsciiFastPathDisabled &&
            _singleShift < 0 &&
            GetActiveCharacterSet() == TerminalCharacterSet.Ascii &&
            _pendingClusterText.Length == 0 &&
            !EndsWithZeroWidthJoiner(_lastPrintedClusterText);
    }

    // Returns the number of leading UTF-16 units in <paramref name="text"/> that are printable ASCII
    // (U+0020..U+007E). Each such unit is a standalone, width-1, non-combining cell. SIMD scans whole
    // vectors at a time and falls back to a scalar loop for the tail and for the block that contains
    // the first non-printable character.
    private static int ScanPrintableAsciiRun(ReadOnlySpan<char> text)
    {
        int index = 0;
        int length = text.Length;

        if (SimdVector.IsHardwareAccelerated && length >= Vector<ushort>.Count)
        {
            ReadOnlySpan<ushort> units = MemoryMarshal.Cast<char, ushort>(text);
            var lowerBound = new Vector<ushort>(PrintableAsciiMin);
            var range = new Vector<ushort>((ushort)(PrintableAsciiMax - PrintableAsciiMin));
            int limit = length - Vector<ushort>.Count;
            while (index <= limit)
            {
                var block = new Vector<ushort>(units.Slice(index, Vector<ushort>.Count));
                // (c - 0x20) <= (0x7E - 0x20) as an unsigned comparison: characters below 0x20 wrap
                // to a large value and characters above 0x7E exceed the range, so both break the run.
                if (SimdVector.GreaterThanAny(block - lowerBound, range))
                {
                    break;
                }

                index += Vector<ushort>.Count;
            }
        }

        while (index < length)
        {
            if ((uint)(text[index] - PrintableAsciiMin) > (uint)(PrintableAsciiMax - PrintableAsciiMin))
            {
                break;
            }

            index++;
        }

        return index;
    }

    private void WritePrintableAsciiRun(ReadOnlySpan<char> run)
    {
        for (int index = 0; index < run.Length; index++)
        {
            PutText(PrintableAsciiStrings[run[index] - PrintableAsciiMin], width: 1);
        }
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
        bool combinedSizeChanged = _combinedRenderCache.Length != totalLineCount;
        if (_renderCacheDirty || _scrollbackCombinedCacheDirty || combinedSizeChanged)
        {
            if (combinedSizeChanged)
            {
                _combinedRenderCache = new TerminalRenderLineSnapshot[totalLineCount];
            }

            if ((combinedSizeChanged || _scrollbackCombinedCacheDirty) && renderScrollbackCount > 0)
            {
                _scrollbackRenderCache.CopyTo(_combinedRenderCache, 0);
            }

            if ((combinedSizeChanged || _renderCacheDirty) && visibleScreenLineCount > 0)
            {
                Array.Copy(
                    _screenRenderCache,
                    0,
                    _combinedRenderCache,
                    renderScrollbackCount,
                    visibleScreenLineCount);
            }

            _renderCacheDirty = false;
            _scrollbackCombinedCacheDirty = false;
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
        _screenStore.ClearScrollback();
        _scrollbackRenderCache.Clear();
        _renderCacheDirty = true;
        _scrollbackCombinedCacheDirty = true;
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

    private void RebuildScrollbackRenderCache()
    {
        _scrollbackRenderCache.Clear();
        foreach (TerminalLine line in _scrollback)
        {
            _scrollbackRenderCache.Add(CreateLineSnapshot(line, -1, -1, showCursor: false));
        }

        _scrollbackCombinedCacheDirty = true;
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
        _screenStore.ReplaceScreen(CreateScreen(_rows, _columns, TerminalStyle.Default));
        _primaryScreenBackup = null;
        _screenStore.ResetAlternateState();
        _cursorRow = 0;
        _cursorColumn = 0;
        _wrapPending = false;
        _savedCursorRow = 0;
        _savedCursorColumn = 0;
        _savedWrapPending = false;
        _currentStyle = TerminalStyle.Default;
        _savedStyle = TerminalStyle.Default;
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
        _glLevel = 0;
        _savedGlLevel = 0;
        _singleShift = -1;
        _lineFeedNewlineMode = false;
        _altSendsEscape = true;
        _eightBitInput = false;
        _autoRepeatKeys = true;
        _reverseWraparound = false;
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
        _g2CharacterSet = TerminalCharacterSet.Ascii;
        _g3CharacterSet = TerminalCharacterSet.Ascii;
        _savedG0CharacterSet = TerminalCharacterSet.Ascii;
        _savedG1CharacterSet = TerminalCharacterSet.Ascii;
        _savedG2CharacterSet = TerminalCharacterSet.Ascii;
        _savedG3CharacterSet = TerminalCharacterSet.Ascii;
        _currentHyperlink = null;
        _savedHyperlink = null;
        _pendingSyntheticAlternateScreenBackup = null;
        _screenStore.ClearPendingPrimaryScreen();
        _windowTitle = string.Empty;
        _windowTitleStack.Clear();
        _lastPrintedClusterText = string.Empty;
        _lastPrintedClusterWidth = 0;
        ClearPendingCluster();
        _parser.Reset();
        Array.Copy(_defaultAnsiPalette, _ansiPalette, _defaultAnsiPalette.Length);
        _kittyKeyboardFlags = 0;
        _kittyKeyboardStack.Clear();
        ResetTabStops();
        ResetMargins();
        ResetScreenRenderCache();
    }

    private void ProcessControl(char ch)
    {
        switch (ch)
        {
            case '\u0005':
                EmitInputSequence(_answerbackString);
                break;
            case '\u0007':
                BellReceived?.Invoke(this, EventArgs.Empty);
                break;
            case '\u000E':
                _glLevel = 1;
                break;
            case '\u000F':
                _glLevel = 0;
                break;
            case '\u008E':
                _singleShift = 2;
                break;
            case '\u008F':
                _singleShift = 3;
                break;
            case '\u0084':
                ClearWrapPending();
                MoveDownAndScrollIfNeeded();
                break;
            case '\u0085':
                ClearWrapPending();
                MoveDownAndScrollIfNeeded();
                _cursorColumn = 0;
                break;
            case '\u0088':
                SetTabStopAtCursor();
                break;
            case '\u008D':
                ClearWrapPending();
                ReverseIndex();
                break;
            case '\r':
                ClearWrapPending();
                _cursorColumn = 0;
                break;
            case '\n':
            case '\u000b':
            case '\u000c':
                ClearWrapPending();
                LineFeed();
                break;
            case '\b':
                ClearWrapPending();
                Backspace();
                break;
            case '\t':
                ClearWrapPending();
                _cursorColumn = FindNextTabStop(_cursorColumn);
                break;
        }
    }

    private void ClearWrapPending() => _wrapPending = false;

    private void Backspace()
    {
        ClearWrapPending();
        int leftBound = _leftRightMarginEnabled ? _leftMargin : 0;
        if (_cursorColumn > leftBound)
        {
            _cursorColumn--;
            return;
        }

        // DEC private mode 45: backspacing past the left edge wraps to the end of the previous line.
        if (_reverseWraparound && _cursorRow > GetTopRowLimit())
        {
            _cursorRow--;
            _cursorColumn = _leftRightMarginEnabled ? _rightMargin : _columns - 1;
            return;
        }

        _cursorColumn = leftBound;
    }

    private void ProcessRune(Rune rune)
    {
        Rune mappedRune = MapActiveRune(rune);
        if (_singleShift >= 0)
        {
            _singleShift = -1;
        }

        int width = TerminalWidthCalculator.GetWidth(mappedRune, _ambiguousWidthIsWide);
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

    private void ProcessEscapeCommand(char ch)
    {
        if (ch is not ('7' or '8'))
        {
            ClearWrapPending();
        }

        switch (ch)
        {
            case 'n':
                _glLevel = 2;
                break;
            case 'o':
                _glLevel = 3;
                break;
            case 'N':
                _singleShift = 2;
                break;
            case 'O':
                _singleShift = 3;
                break;
            case '7':
                SaveCursorState();
                break;
            case '8':
                RestoreCursorState();
                break;
            case 'D':
                MoveDownAndScrollIfNeeded();
                break;
            case 'E':
                MoveDownAndScrollIfNeeded();
                _cursorColumn = 0;
                break;
            case 'M':
                ReverseIndex();
                break;
            case 'H':
                SetTabStopAtCursor();
                break;
            case '=':
                _applicationKeypad = true;
                break;
            case '>':
                _applicationKeypad = false;
                break;
            case 'c':
                ResetTerminal();
                break;
        }
    }

    private void ProcessCharsetDesignation(int target, char ch)
    {
        TerminalCharacterSet characterSet = ch switch
        {
            '0' => TerminalCharacterSet.DecSpecialGraphics,
            _ => TerminalCharacterSet.Ascii
        };

        switch (target)
        {
            case 0:
                _g0CharacterSet = characterSet;
                break;
            case 1:
                _g1CharacterSet = characterSet;
                break;
            case 2:
                _g2CharacterSet = characterSet;
                break;
            case 3:
                _g3CharacterSet = characterSet;
                break;
        }
    }

    private void ProcessDecLineSize(char ch)
    {
        ClearWrapPending();
        // ESC # <ch>: DEC line-size / alignment controls.
        // ESC # 8 = DECALN: fill the entire screen with 'E' in the default rendition and home the cursor.
        // ESC # 3/4/5/6 = DECDHL/DECDWL/DECSWL double-height/width lines.
        if (ch == '8')
        {
            FillScreenForAlignment();
            return;
        }

        TerminalLineSize? lineSize = ch switch
        {
            '3' => TerminalLineSize.DoubleHeightTop,
            '4' => TerminalLineSize.DoubleHeightBottom,
            '5' => TerminalLineSize.SingleWidth,
            '6' => TerminalLineSize.DoubleWidth,
            _ => null
        };
        if (lineSize.HasValue)
        {
            _screen[_cursorRow].LineSize = lineSize.Value;
            InvalidateScreenRenderCache();
        }
    }

    private void FillScreenForAlignment()
    {
        _screenStore.FillAlignment();
        _cursorRow = 0;
        _cursorColumn = 0;
        _wrapPending = false;
        InvalidateScreenRenderCache();
    }

    private void DispatchDcs(string content)
    {
        DcsCommand command = DcsDecoder.Decode(content);
        if (command.Kind == DcsCommandKind.Decrqss)
        {
            string? status = command.RequestToken switch
            {
                " q" => $"{GetCursorStyleParameter()} q", // DECSCUSR
                "m" => SerializeCurrentSgr(),              // SGR
                "r" => $"{_scrollTop + 1};{_scrollBottom + 1}r", // DECSTBM
                "s" => $"{_leftMargin + 1};{_rightMargin + 1}s", // DECSLRM
                "\"p" => "62;1\"p",                    // DECSCL: VT220, 7-bit controls
                "\"q" => "0\"q",                       // DECSCA: all cells erasable
                "t" => $"{_rows}t",                       // DECSLPP
                "$|" => $"{_columns}$|",                  // DECSCPP
                "*|" => $"{_rows}*|",                     // DECSNLS
                ">4m" => $">4;{_modifyOtherKeys}m",       // XTQMODKEYS for modifyOtherKeys
                _ => null
            };

            EmitInputSequence(status is null
                ? "P0$r\\"
                : $"P1$r{status}\\");

            return;
        }

        if (command.Kind == DcsCommandKind.XtGetTcap)
        {
            DispatchXtGetTcap(command.Payload!);
            return;
        }

        if (command.Kind == DcsCommandKind.XtSetTcap)
        {
            DispatchXtSetTcap(command.Payload!);
            return;
        }

        if (command.Kind == DcsCommandKind.Sixel)
        {
            // Sixel graphics are not supported; the sequence is consumed and ignored.
            return;
        }

        _unknownDcsSequenceCount++;
    }

    private void DispatchXtGetTcap(string encodedNames)
    {
        var pairs = new List<string>();
        foreach (string encodedName in encodedNames.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TryDecodeHex(encodedName, out string name))
            {
                break;
            }

            string? value = GetTermcapValue(name);
            if (value is null)
            {
                break;
            }

            pairs.Add($"{encodedName}={EncodeHex(value)}");
        }

        if (pairs.Count == 0)
        {
            EmitInputSequence("P0+r\\");
            return;
        }

        EmitInputSequence($"P1+r{string.Join(';', pairs)}\\");
    }

    private void DispatchXtSetTcap(string encodedValue)
    {
        if (!TryDecodeHex(encodedValue, out string decoded))
        {
            return;
        }

        int separator = decoded.IndexOf('=');
        if (separator <= 0)
        {
            return;
        }

        _termcapOverrides[decoded[..separator]] = decoded[(separator + 1)..];
    }

    private string? GetTermcapValue(string name)
    {
        if (_termcapOverrides.TryGetValue(name, out string? overrideValue))
        {
            return overrideValue;
        }

        return name switch
        {
            "TN" => "xterm-256color",
            "Co" => "256",
            "RGB" or "Tc" => "1",
            "kcuu1" => "[A",
            "kcud1" => "[B",
            "kcuf1" => "[C",
            "kcub1" => "[D",
            "khome" => "[H",
            "kend" => "[F",
            "kich1" => "[2~",
            "kdch1" => "[3~",
            "kpp" => "[5~",
            "knp" => "[6~",
            "kf1" => "OP",
            "kf2" => "OQ",
            "kf3" => "OR",
            "kf4" => "OS",
            "kf5" => "[15~",
            "kf6" => "[17~",
            "kf7" => "[18~",
            "kf8" => "[19~",
            "kf9" => "[20~",
            "kf10" => "[21~",
            "kf11" => "[23~",
            "kf12" => "[24~",
            _ => null
        };
    }

    private static bool TryDecodeHex(string encoded, out string value)
    {
        value = string.Empty;
        if (encoded.Length == 0 || (encoded.Length & 1) != 0)
        {
            return false;
        }

        try
        {
            value = Encoding.UTF8.GetString(Convert.FromHexString(encoded));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string EncodeHex(string value)
    {
        return Convert.ToHexString(Encoding.UTF8.GetBytes(value));
    }

    private string SerializeCurrentSgr()
    {
        if (_currentStyle == TerminalStyle.Default)
        {
            return "0m";
        }

        // Start with a reset so the response describes the complete rendition rather than a delta.
        // Color origin (ANSI/256/RGB) is not retained in TerminalStyle, so emit exact RGB values.
        var parameters = new List<string> { "0" };
        if (_currentStyle.Bold) parameters.Add("1");
        if (_currentStyle.Dim) parameters.Add("2");
        if (_currentStyle.Italic) parameters.Add("3");
        if (_currentStyle.UnderlineStyle != UnderlineStyle.None)
        {
            parameters.Add(_currentStyle.UnderlineStyle switch
            {
                UnderlineStyle.Single => "4",
                UnderlineStyle.Double => "4:2",
                UnderlineStyle.Curly => "4:3",
                UnderlineStyle.Dotted => "4:4",
                UnderlineStyle.Dashed => "4:5",
                _ => "4"
            });
        }

        if (_currentStyle.Blink) parameters.Add("5");
        if (_currentStyle.Inverse) parameters.Add("7");
        if (_currentStyle.Invisible) parameters.Add("8");
        if (_currentStyle.Strikethrough) parameters.Add("9");
        if (_currentStyle.Overline) parameters.Add("53");
        if (_currentStyle.Foreground is Color foreground)
        {
            parameters.Add($"38;2;{foreground.R};{foreground.G};{foreground.B}");
        }

        if (_currentStyle.Background is Color background)
        {
            parameters.Add($"48;2;{background.R};{background.G};{background.B}");
        }

        if (_currentStyle.UnderlineColor is Color underline)
        {
            parameters.Add($"58;2;{underline.R};{underline.G};{underline.B}");
        }

        return $"{string.Join(';', parameters)}m";
    }

    private void DispatchOsc(string content)
    {
        OscCommand oscCommand = OscDecoder.Decode(content);
        string command = oscCommand.Command;
        string value = oscCommand.Value;
        if (command.Length == 0)
        {
            return;
        }

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

        if (command is "10" or "11" or "12")
        {
            DispatchOscDynamicColor(command, value);
            return;
        }

        if (command is "110" or "111" or "112")
        {
            DispatchOscDynamicColorReset(command);
            return;
        }

        if (command == "9")
        {
            // ConEmu OSC 9;4 はタスクバー進捗。それ以外の OSC 9 はデスクトップ通知。
            // Windows Terminal と同じく "4;" プレフィックスで判別する。
            if (value == "4" || value.StartsWith("4;", StringComparison.Ordinal))
            {
                DispatchOscTaskbarProgress(value);
                return;
            }

            if (!string.IsNullOrEmpty(value))
            {
                NotificationRequested?.Invoke(this, value);
            }

            return;
        }

        if (command == "7")
        {
            if (OscDecoder.TryDecodeCurrentDirectory(value, out string localPath))
            {
                CurrentDirectoryChanged?.Invoke(this, localPath);
            }

            return;
        }

        if (command == "4")
        {
            DispatchOscPaletteChange(value);
            return;
        }

        if (command == "104")
        {
            DispatchOscPaletteReset(value);
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

    private void DispatchOscTaskbarProgress(string value)
    {
        if (!OscDecoder.TryDecodeTaskbarProgress(value, out OscTaskbarProgress progress))
        {
            return;
        }

        TaskbarProgressChanged?.Invoke(
            this,
            new TaskbarProgressEventArgs(progress.State, progress.Progress));
    }

    private void DispatchOscPaletteChange(string value)
    {
        foreach (OscPaletteChange change in OscDecoder.DecodePaletteChanges(value, _ansiPalette.Length))
        {
            if (change.Kind == OscPaletteChangeKind.Query)
            {
                EmitInputSequence(
                    $"]4;{change.Index};{OscDecoder.FormatColor(_ansiPalette[change.Index])}");
            }
            else
            {
                _ansiPalette[change.Index] = change.Color;
                InvalidateScreenRenderCache();
            }
        }
    }

    // OSC 10 (foreground) / 11 (background) / 12 (cursor): query with "?" or set from an rgb:/# color spec.
    private void DispatchOscDynamicColor(string command, string value)
    {
        if (value == "?")
        {
            Color queryColor = command switch
            {
                "10" => _defaultForeground,
                "11" => _defaultBackground,
                _ => _cursorAccent
            };
            EmitInputSequence($"]{command};{OscDecoder.FormatColor(queryColor)}");
            return;
        }

        // A set request may carry several ';'-separated specs; only the first applies to this OSC index.
        int specEnd = value.IndexOf(';');
        string spec = specEnd >= 0 ? value[..specEnd] : value;
        if (!OscDecoder.TryParseColor(spec, out Color parsed))
        {
            return;
        }

        switch (command)
        {
            case "10":
                _defaultForeground = parsed;
                break;
            case "11":
                _defaultBackground = parsed;
                break;
            default:
                _cursorAccent = parsed;
                break;
        }

        InvalidateScreenRenderCache();
    }

    // OSC 110 (foreground) / 111 (background) / 112 (cursor): reset the dynamic color to the theme default.
    private void DispatchOscDynamicColorReset(string command)
    {
        switch (command)
        {
            case "110":
                _defaultForeground = _themeForeground;
                break;
            case "111":
                _defaultBackground = _themeBackground;
                break;
            default:
                _cursorAccent = _themeCursor;
                break;
        }

        InvalidateScreenRenderCache();
    }

    // OSC 104: reset one or more ANSI palette entries to the theme default. With no parameter, resets all.
    private void DispatchOscPaletteReset(string value)
    {
        OscPaletteReset reset = OscDecoder.DecodePaletteReset(value, _ansiPalette.Length);
        if (reset.ResetAll)
        {
            Array.Copy(_defaultAnsiPalette, _ansiPalette, _defaultAnsiPalette.Length);
            InvalidateScreenRenderCache();
            return;
        }

        foreach (int paletteIndex in reset.Indices)
        {
            _ansiPalette[paletteIndex] = _defaultAnsiPalette[paletteIndex];
            InvalidateScreenRenderCache();
        }
    }

    private void DispatchOscShellIntegration(string value)
    {
        OscShellPayload payload = OscDecoder.DecodeShellIntegration(value);
        switch (payload.Kind)
        {
            case OscShellPayloadKind.CommandLine:
                ShellCommandLineReceived?.Invoke(this, payload.Value!);
                break;
            case OscShellPayloadKind.Property:
                if (payload.PropertyName!.Equals("HistoryPath", StringComparison.OrdinalIgnoreCase))
                {
                    ShellHistoryPathReceived?.Invoke(this, payload.Value!);
                }

                break;
            case OscShellPayloadKind.Zone:
                int absoluteLine = _scrollback.Count + _cursorRow;
                ShellCommandZoneReceived?.Invoke(
                    this,
                    new ShellCommandZoneEventArgs(payload.ZoneType!.Value, absoluteLine, payload.ExitCode));
                break;
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
        OscClipboardPayload payload = OscDecoder.DecodeClipboard(value);
        if (payload.Kind == OscClipboardKind.Query)
        {
            ClipboardQueryRequested?.Invoke(this, payload.SelectionTargets);
            return;
        }

        if (payload.Kind == OscClipboardKind.Set)
        {
            ClipboardSetRequested?.Invoke(this, payload.Text!);
        }
    }

    private void DecodeCsi(char final, string rawParameters)
    {
        ClearWrapPending();
        DispatchCsi(CsiDecoder.Decode(final, rawParameters));
    }

    private void DispatchCsi(CsiCommand command)
    {
        bool isPrivate = command.IsPrivate;
        bool isSecondary = command.IsSecondary;
        string intermediate = command.Intermediate;
        string paramText = command.ParameterText;
        int?[] parameters = command.Parameters;

        switch (command.Final)
        {
            case '@':
                if (intermediate == " ")
                {
                    ScrollLeft(GetParameter(parameters, 0, 1));
                }
                else
                {
                    InsertCharacters(GetParameter(parameters, 0, 1));
                }

                break;
            case 'A':
                if (intermediate == " ")
                {
                    ScrollRight(GetParameter(parameters, 0, 1));
                }
                else
                {
                    _cursorRow = Math.Max(GetTopRowLimit(), _cursorRow - GetParameter(parameters, 0, 1));
                }

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
            case 'a':
                {
                    int rightLimit = _leftRightMarginEnabled ? _rightMargin : _columns - 1;
                    _cursorColumn = Math.Min(
                        rightLimit,
                        _cursorColumn + GetParameter(parameters, 0, 1));
                    break;
                }
            case 'b':
                RepeatLastPrintedCluster(GetParameter(parameters, 0, 1));
                break;
            case 'd':
                SetCursorRow(GetParameter(parameters, 0, 1));
                break;
            case 'e':
                _cursorRow = Math.Min(
                    GetBottomRowLimit(),
                    _cursorRow + GetParameter(parameters, 0, 1));
                break;
            case 'c':
                DispatchDeviceAttributes(isPrivate, isSecondary);
                break;
            case 'g':
                ClearTabStops(GetParameter(parameters, 0, 0));
                break;
            case 'i':
                // Media Copy (printing, e.g. CSI 5i / CSI 4i / CSI ?5i). No printer is attached,
                // so the sequence is explicitly consumed without side effects rather than printed.
                break;
            case 'h':
            case 'l':
                if (isPrivate)
                {
                    SetPrivateMode(parameters, command.Final == 'h');
                }
                else
                {
                    SetMode(parameters, command.Final == 'h');
                }

                break;
            case 'm':
                if (isSecondary && GetParameter(parameters, 0, -1) == 4)
                {
                    _modifyOtherKeys = GetParameter(parameters, 1, 0);
                }
                else if (!isSecondary)
                {
                    ApplySgr(SgrInterpreter.Parse(paramText));
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
                else if (command.RawParameters.Length > 0 && command.RawParameters[0] == '<')
                {
                    KittyPopFlags(GetParameter(
                        CsiDecoder.ParseParameterList(command.RawParameters[1..]),
                        0,
                        1));
                }
                else if (command.RawParameters.Length > 0 && command.RawParameters[0] == '=')
                {
                    int?[] modeParameters = CsiDecoder.ParseParameterList(command.RawParameters[1..]);
                    KittySetFlags(
                        GetParameter(modeParameters, 0, 0),
                        GetParameter(modeParameters, 1, 1));
                }
                else
                {
                    RestoreCursorState();
                }

                break;
            case '}':
                if (intermediate == "'")
                {
                    InsertColumns(GetParameter(parameters, 0, 1));
                }

                break;
            case '~':
                if (intermediate == "'")
                {
                    DeleteColumns(GetParameter(parameters, 0, 1));
                }

                break;
            default:
                _unknownCsiSequenceCount++;
                break;
        }
    }

    private void SaveCursorState()
    {
        _savedCursorRow = _cursorRow;
        _savedCursorColumn = _cursorColumn;
        _savedWrapPending = _wrapPending;
        _savedStyle = _currentStyle;
        _savedGlLevel = _glLevel;
        _savedG0CharacterSet = _g0CharacterSet;
        _savedG1CharacterSet = _g1CharacterSet;
        _savedG2CharacterSet = _g2CharacterSet;
        _savedG3CharacterSet = _g3CharacterSet;
        _savedInsertMode = _insertMode;
        _savedOriginMode = _originMode;
        _savedAutoWrapEnabled = _autoWrapEnabled;
        _savedHyperlink = _currentHyperlink;
    }

    private void RestoreCursorState()
    {
        _cursorRow = Math.Clamp(_savedCursorRow, 0, _rows - 1);
        _cursorColumn = Math.Clamp(_savedCursorColumn, 0, _columns - 1);
        _wrapPending = _savedWrapPending && _autoWrapEnabled;
        _currentStyle = _savedStyle;
        _glLevel = _savedGlLevel;
        _g0CharacterSet = _savedG0CharacterSet;
        _g1CharacterSet = _savedG1CharacterSet;
        _g2CharacterSet = _savedG2CharacterSet;
        _g3CharacterSet = _savedG3CharacterSet;
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

    // DA1 reports VT220 (62) advertising 132-column (1) and ANSI color (22). Sixel (attribute 4)
    // is intentionally omitted: Sixel graphics are not rendered, so advertising it would invite
    // applications to emit data that is silently dropped.
    private void DispatchDeviceAttributes(bool isPrivate, bool isSecondary)
    {
        if (isSecondary)
        {
            EmitInputSequence("\u001b[>0;10;1c");
            return;
        }

        if (isPrivate)
        {
            EmitInputSequence("\u001b[?62;1;22c");
            return;
        }

        EmitInputSequence("\u001b[?62;1;22c");
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
                    if (!_autoWrapEnabled)
                    {
                        ClearWrapPending();
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
                case 8:
                    _autoRepeatKeys = enabled;
                    break;
                case 45:
                    _reverseWraparound = enabled;
                    break;
                case 1034:
                    _eightBitInput = enabled;
                    break;
                case 1036:
                case 1039:
                    _altSendsEscape = enabled;
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
                case 20:
                    _lineFeedNewlineMode = enabled;
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

    // Inverse of SetCursorStyle: maps the current shape + blink state back to a DECSCUSR Ps value.
    private int GetCursorStyleParameter()
    {
        return _cursorShape switch
        {
            TerminalCursorShape.Underline => _cursorBlinkEnabled ? 3 : 4,
            TerminalCursorShape.Bar => _cursorBlinkEnabled ? 5 : 6,
            _ => _cursorBlinkEnabled ? 1 : 2
        };
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
            8 => _autoRepeatKeys ? 1 : 2,
            45 => _reverseWraparound ? 1 : 2,
            1034 => _eightBitInput ? 1 : 2,
            1036 or 1039 => _altSendsEscape ? 1 : 2,
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
            case 22:
                PushWindowTitle();
                break;
            case 23:
                PopWindowTitle();
                break;
        }
    }

    private const int MaxWindowTitleStackDepth = 128;

    // XTWINOPS CSI 22 t: push the current window title. The icon/window sub-selector (Ps2) is
    // ignored because only a single window title is tracked. A depth cap guards against a runaway
    // program pushing without ever popping.
    private void PushWindowTitle()
    {
        if (_windowTitleStack.Count >= MaxWindowTitleStackDepth)
        {
            return;
        }

        _windowTitleStack.Push(_windowTitle);
    }

    // XTWINOPS CSI 23 t: restore the most recently pushed window title. A pop with an empty stack
    // is a no-op, matching xterm.
    private void PopWindowTitle()
    {
        if (_windowTitleStack.Count == 0)
        {
            return;
        }

        string previousTitle = _windowTitle;
        _windowTitle = _windowTitleStack.Pop();
        UpdateSyntheticAlternateScreenFromTitle(previousTitle, _windowTitle);
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
        _glLevel = 0;
        _singleShift = -1;
        _lineFeedNewlineMode = false;
        _useUtf8MouseEncoding = false;
        _useSgrMouseEncoding = false;
        _useUrxvtMouseEncoding = false;
        _mousePixelMode = false;
        _screenReverse = false;
        _mouseTrackingMode = TerminalMouseTrackingMode.Off;
        _synchronizedUpdateActive = false;
        _leftRightMarginEnabled = false;
        _pendingSyntheticAlternateScreenBackup = null;
        _screenStore.ClearPendingPrimaryScreen();
        _g0CharacterSet = TerminalCharacterSet.Ascii;
        _g1CharacterSet = TerminalCharacterSet.Ascii;
        _g2CharacterSet = TerminalCharacterSet.Ascii;
        _g3CharacterSet = TerminalCharacterSet.Ascii;
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
            8 => _autoRepeatKeys,
            45 => _reverseWraparound,
            1034 => _eightBitInput,
            1036 or 1039 => _altSendsEscape,
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
        _screenStore.EnterAlternateScreen(_rows, _columns);
        _cursorRow = 0;
        _cursorColumn = 0;
        _wrapPending = false;
        _savedCursorRow = 0;
        _savedCursorColumn = 0;
        _savedWrapPending = false;
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

        _screenStore.ExitAlternateScreen();
        _tabStops = (bool[])backup.TabStops.Clone();
        _rows = backup.Rows;
        _columns = backup.Columns;
        _cursorRow = backup.CursorRow;
        _cursorColumn = backup.CursorColumn;
        _wrapPending = backup.WrapPending && _autoWrapEnabled;
        _savedCursorRow = backup.SavedCursorRow;
        _savedCursorColumn = backup.SavedCursorColumn;
        _savedWrapPending = backup.SavedWrapPending && _autoWrapEnabled;
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
        _screenStore.CapturePendingPrimaryScreen();
    }

    private ScreenState CaptureScreenState()
    {
        return new ScreenState(
            (bool[])_tabStops.Clone(),
            _rows,
            _columns,
            _cursorRow,
            _cursorColumn,
            _wrapPending,
            _savedCursorRow,
            _savedCursorColumn,
            _savedWrapPending,
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
            _screenStore.ClearPendingPrimaryScreen();
            return;
        }

        if (_primaryScreenBackup is not null)
        {
            return;
        }

        _primaryScreenBackup = _pendingSyntheticAlternateScreenBackup ?? CaptureScreenState();
        _screenStore.PromotePendingOrCapturePrimaryScreen();
        _pendingSyntheticAlternateScreenBackup = null;
        _syntheticAlternateScreenActive = true;
        InvalidateScreenRenderCache();
    }

    private static bool IsClaudeSyntheticAlternateScreenTitle(string title)
    {
        return title.Equals("claude", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Claude Code", StringComparison.OrdinalIgnoreCase);
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
                    _currentStyle = _currentStyle with
                    {
                        UnderlineStyle = SgrInterpreter.ResolveUnderlineStyle(tokens[index])
                    };
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
                    if (SgrInterpreter.TryReadExtendedColor(
                            tokens,
                            ref index,
                            _ansiPalette,
                            _defaultForeground,
                            out Color color))
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
        _screenStore.ReplaceScreen(TerminalReflowCalculator.ResizeScreenBuffer(
            _screen,
            _rows,
            _screen[0].Cells.Length,
            _rows,
            _columns,
            preserveBottomRows: false));
        _tabStops = CreateDefaultTabStops(_columns);
        _cursorRow = 0;
        _cursorColumn = 0;
        _wrapPending = false;
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
        _screenStore.InsertLines(
            _cursorRow,
            _scrollTop,
            _scrollBottom,
            count,
            _columns,
            _currentStyle);
    }

    private void DeleteLines(int count)
    {
        _screenStore.DeleteLines(
            _cursorRow,
            _scrollTop,
            _scrollBottom,
            count,
            _columns,
            _currentStyle);
    }

    private void InsertCharacters(int count)
    {
        int rightLimit = _leftRightMarginEnabled ? _rightMargin + 1 : _columns;
        _screenStore.InsertCharacters(_cursorRow, _cursorColumn, rightLimit, count, _currentStyle);
    }

    private void ScrollLeft(int count)
    {
        _screenStore.ScrollLeft(
            GetTopRowLimit(),
            GetBottomRowLimit(),
            _leftRightMarginEnabled ? _leftMargin : 0,
            _leftRightMarginEnabled ? _rightMargin + 1 : _columns,
            count,
            _currentStyle);
    }

    private void ScrollRight(int count)
    {
        _screenStore.ScrollRight(
            GetTopRowLimit(),
            GetBottomRowLimit(),
            _leftRightMarginEnabled ? _leftMargin : 0,
            _leftRightMarginEnabled ? _rightMargin + 1 : _columns,
            count,
            _currentStyle);
    }

    private void InsertColumns(int count)
    {
        _screenStore.InsertColumns(
            GetTopRowLimit(),
            GetBottomRowLimit(),
            _leftRightMarginEnabled ? _leftMargin : 0,
            _leftRightMarginEnabled ? _rightMargin + 1 : _columns,
            count,
            _currentStyle);
    }

    private void DeleteColumns(int count)
    {
        _screenStore.DeleteColumns(
            GetTopRowLimit(),
            GetBottomRowLimit(),
            _leftRightMarginEnabled ? _leftMargin : 0,
            _leftRightMarginEnabled ? _rightMargin + 1 : _columns,
            count,
            _currentStyle);
    }

    private void DeleteCharacters(int count)
    {
        int rightLimit = _leftRightMarginEnabled ? _rightMargin + 1 : _columns;
        _screenStore.DeleteCharacters(_cursorRow, _cursorColumn, rightLimit, count, _currentStyle);
    }

    private void EraseCharacters(int count)
    {
        _screenStore.EraseCharacters(_cursorRow, _cursorColumn, count, _columns, _currentStyle);
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
                _screenStore.FillRange(_cursorRow, _cursorColumn, rightLimit, _columns, _currentStyle);
                break;
            case 1:
                _screenStore.FillRange(_cursorRow, leftLimit, _cursorColumn + 1, _columns, _currentStyle);
                break;
            case 2:
                _screenStore.FillRange(_cursorRow, leftLimit, rightLimit, _columns, _currentStyle);
                break;
        }
    }

    private void ClearEntireLine(int row)
    {
        _screenStore.FillRange(row, 0, _columns, _columns, _currentStyle, clearWrapped: true);
    }

    private void PutText(string text, int width)
    {
        int normalizedWidth = Math.Clamp(width, 1, 2);
        if (_wrapPending)
        {
            if (_autoWrapEnabled)
            {
                _screen[_cursorRow].IsWrapped = true;
                _cursorColumn = 0;
                MoveDownAndScrollIfNeeded();
            }

            _wrapPending = false;
        }

        if (normalizedWidth == 2 && _cursorColumn == _columns - 1)
        {
            if (_autoWrapEnabled)
            {
                _screen[_cursorRow].IsWrapped = true;
                _cursorColumn = 0;
                MoveDownAndScrollIfNeeded();
            }
            else
            {
                return;
            }
        }

        if (_insertMode)
        {
            InsertCharacters(normalizedWidth);
        }

        _screenStore.PlaceCell(
            _cursorRow,
            _cursorColumn,
            text,
            normalizedWidth,
            _columns,
            _currentStyle,
            _currentHyperlink);

        if (normalizedWidth == 2)
        {
            if (_cursorColumn + 1 >= _columns)
            {
                _cursorColumn = _columns - 1;
                _wrapPending = _autoWrapEnabled;
                _lastPrintedClusterText = text;
                _lastPrintedClusterWidth = normalizedWidth;
                return;
            }
        }

        _cursorColumn += normalizedWidth;
        if (_cursorColumn >= _columns)
        {
            _cursorColumn = _columns - 1;
            _wrapPending = _autoWrapEnabled;
        }

        _lastPrintedClusterText = text;
        _lastPrintedClusterWidth = normalizedWidth;
    }

    private void AppendCombiningRune(Rune rune)
    {
        TerminalLine targetLine = _screen[_cursorRow];
        int targetColumn = _cursorColumn > 0 ? _cursorColumn - 1 : FindLastOccupiedColumn(targetLine);

        if (targetColumn < 0)
        {
            // Cursor is at column 0 with no previous character on this line.
            // After an autowrap the base character sits at the end of the previous row.
            if (_cursorRow > 0 && _screen[_cursorRow - 1].IsWrapped)
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
            int nextColumn = Math.Max(_cursorColumn, targetColumn + 2);
            if (nextColumn >= _columns)
            {
                _cursorColumn = _columns - 1;
                _wrapPending = _autoWrapEnabled;
            }
            else
            {
                _cursorColumn = nextColumn;
            }
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

    private void MoveDownAndScrollIfNeeded()
    {
        if (_cursorRow == _scrollBottom)
        {
            ScrollUpRegion(1, _scrollTop, _scrollBottom);
            return;
        }

        _cursorRow = Math.Min(_rows - 1, _cursorRow + 1);
    }

    // C0 line feeds (LF / VT / FF). When LNM (mode 20) is set they also carry the cursor to column 0.
    private void LineFeed()
    {
        MoveDownAndScrollIfNeeded();
        if (_lineFeedNewlineMode)
        {
            _cursorColumn = 0;
        }
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
        bool appendToScrollback = _primaryScreenBackup is null && top == 0 && bottom == _rows - 1;
        int previousScrollbackCount = _scrollback.Count;
        int appendedCount = appendToScrollback ? Math.Clamp(lines, 1, bottom - top + 1) : 0;
        TerminalScreenMutation mutation = _screenStore.ScrollUp(
            lines,
            top,
            bottom,
            _columns,
            _currentStyle,
            appendToScrollback);
        if (mutation.ScrollbackChanged)
        {
            UpdateAppendedScrollbackRenderCache(previousScrollbackCount, appendedCount);
            _renderCacheDirty = true;
        }
    }

    private void UpdateAppendedScrollbackRenderCache(int previousCount, int appendedCount)
    {
        int newCount = _scrollback.Count;
        int actualAppendedCount = Math.Min(appendedCount, newCount);
        int retainedPreviousCount = newCount - actualAppendedCount;
        int expectedPreviousCount = Math.Min(previousCount, retainedPreviousCount);
        int evictedCount = Math.Max(0, _scrollbackRenderCache.Count - expectedPreviousCount);
        if (evictedCount > 0)
        {
            _scrollbackRenderCache.RemoveRange(0, evictedCount);
        }

        for (int index = expectedPreviousCount; index < newCount; index++)
        {
            _scrollbackRenderCache.Add(CreateLineSnapshot(_scrollback[index], -1, -1, showCursor: false));
        }

        _scrollbackCombinedCacheDirty = true;
    }

    private void ScrollDownRegion(int lines, int top, int bottom)
    {
        _screenStore.ScrollDown(lines, top, bottom, _columns, _currentStyle);
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
        if (_primaryScreenBackup is not null)
        {
            return _rows - 1;
        }

        // Once output has scrolled into the scrollback, render the active screen at full
        // height so a cleared screen (Ctrl+L / ESC[2J) pushes the scrollback above the
        // viewport instead of leaving stale history glued beneath the prompt. With an empty
        // scrollback (fresh session) keep trimming trailing blanks to avoid a large blank gap.
        if (_scrollback.Count > 0)
        {
            return _rows - 1;
        }

        return FindLastVisibleScreenRow(showCursor);
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
        return TerminalLineSnapshotBuilder.ExtractPlainText(line);
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
        return TerminalLineSnapshotBuilder.CreateSnapshot(
            line,
            cursorColumn,
            anchorColumn,
            showCursor,
            _screenReverse,
            _defaultForeground,
            _defaultBackground,
            _cursorAccent);
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
        // A pending single shift (SS2/SS3) overrides the locking-shift level for one graphic character;
        // it is consumed in ProcessRune after the character is mapped.
        int level = _singleShift >= 0 ? _singleShift : _glLevel;
        return level switch
        {
            1 => _g1CharacterSet,
            2 => _g2CharacterSet,
            3 => _g3CharacterSet,
            _ => _g0CharacterSet
        };
    }

    private static bool IsControlRune(Rune rune)
    {
        return rune.Value < 0x20 || rune.Value == 0x7F || rune.Value is >= 0x80 and <= 0x9F;
    }

    private static bool IsZeroWidthJoiner(Rune rune)
    {
        return rune.Value == 0x200D;
    }

    private static bool IsRegionalIndicator(Rune rune)
    {
        return rune.Value is >= 0x1F1E6 and <= 0x1F1FF;
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

    private sealed record ScreenState(
        bool[] TabStops,
        int Rows,
        int Columns,
        int CursorRow,
        int CursorColumn,
        bool WrapPending,
        int SavedCursorRow,
        int SavedCursorColumn,
        bool SavedWrapPending,
        int ScrollTop,
        int ScrollBottom,
        TerminalStyle Style,
        TerminalStyle SavedStyle,
        string? CurrentHyperlink,
        string? SavedHyperlink,
        int ModifyOtherKeys,
        int KittyKeyboardFlags,
        List<int> KittyStack);

    internal readonly record struct TerminalRenderSnapshot(
        TerminalRenderLineSnapshot[] Lines,
        bool AmbiguousWidthIsWide = false);

    internal readonly record struct TerminalRenderLineSnapshot(
        int AnchorSegmentIndex,
        int CellLength,
        TerminalRenderSegmentSnapshot[] Segments,
        TerminalLineSize LineSize = TerminalLineSize.SingleWidth)
    {
        public bool ContentEquals(TerminalRenderLineSnapshot other)
        {
            if (AnchorSegmentIndex != other.AnchorSegmentIndex ||
                CellLength != other.CellLength ||
                LineSize != other.LineSize ||
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
        string? Hyperlink,
        bool Blink = false);

    internal readonly record struct TerminalDocumentSnapshot(
        FlowDocument Document,
        FrameworkElement? CursorAnchor);
}
