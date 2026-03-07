using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ConPtyTerminal;

public partial class MainWindow : Window
{
    private const string BaseWindowTitle = "ConPTY Terminal";
    private const int MaxAutoRecoveryAttempts = 1;

    private static readonly TimeSpan InitialOutputTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan IdleOutputTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CursorBlinkInterval = TimeSpan.FromMilliseconds(530);

    private ITerminalSession? _session;
    private AnsiTerminalBuffer _terminalBuffer = new(120, 30);
    private short _currentColumns = 120;
    private short _currentRows = 30;
    private readonly DispatcherTimer _sessionWatchdog = new();
    private readonly DispatcherTimer _cursorBlinkTimer = new();
    private int _autoRecoveryAttempts;
    private bool _isRecovering;
    private bool _isRenderingTerminal;
    private bool _useCompatibilityMode;
    private bool _cursorBlinkVisible = true;

    public MainWindow()
    {
        InitializeComponent();
        CommandTextBox.Text = BuildDefaultCommandLine();

        _sessionWatchdog.Interval = TimeSpan.FromSeconds(1);
        _sessionWatchdog.Tick += SessionWatchdog_Tick;
        _sessionWatchdog.Start();

        _cursorBlinkTimer.Interval = CursorBlinkInterval;
        _cursorBlinkTimer.Tick += CursorBlinkTimer_Tick;
        _cursorBlinkTimer.Start();

        Loaded += OnLoaded;
        Closing += OnClosing;
        Activated += OnActivated;
        Deactivated += OnDeactivated;
        TerminalOutput.SizeChanged += OnTerminalOutputSizeChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RenderTerminal();
        StartTerminal();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _sessionWatchdog.Stop();
        _cursorBlinkTimer.Stop();
        StopTerminal();
    }

    private void OnActivated(object? sender, EventArgs e)
    {
        EmitFocusReport(focused: true);
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        EmitFocusReport(focused: false);
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        _autoRecoveryAttempts = 0;
        _useCompatibilityMode = false;
        StartTerminal();
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _autoRecoveryAttempts = 0;
        StopTerminal();
    }

    private void RecoverButton_Click(object sender, RoutedEventArgs e)
    {
        RecoverSession(isAutomatic: false);
    }

    private void PasteButton_Click(object sender, RoutedEventArgs e)
    {
        PasteFromClipboard();
    }

    private void InterruptButton_Click(object sender, RoutedEventArgs e)
    {
        SendInterrupt();
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        _terminalBuffer.ClearScrollback();
        RenderTerminal();
        SetStatus("Cleared local scrollback.");
    }

    private void TerminalOutput_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!TerminalOutput.IsKeyboardFocusWithin)
        {
            Keyboard.Focus(TerminalOutput);
        }

        if (TrySendMouseButtonEvent(e, pressed: true))
        {
            e.Handled = true;
        }
    }

    private void TerminalOutput_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (TrySendMouseButtonEvent(e, pressed: false))
        {
            e.Handled = true;
        }
    }

    private void TerminalOutput_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (TrySendMouseMoveEvent(e))
        {
            e.Handled = true;
        }
    }

    private void TerminalOutput_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (TrySendMouseWheelEvent(e))
        {
            e.Handled = true;
        }
    }

    private void TerminalOutput_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _cursorBlinkVisible = true;
        if (_isRenderingTerminal)
        {
            return;
        }

        RenderTerminal();
    }

    private void TerminalOutput_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _cursorBlinkVisible = false;
        if (_isRenderingTerminal)
        {
            return;
        }

        RenderTerminal();
    }

    private void TerminalOutput_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text))
        {
            return;
        }

        if (SendTerminalInput(e.Text))
        {
            e.Handled = true;
        }
    }

    private void TerminalOutput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_session is null)
        {
            return;
        }

        if (TryHandleClipboardShortcut(e) || TryHandleControlShortcut(e) || TryHandleApplicationKeypad(e) || TryHandleSpecialKey(e))
        {
            e.Handled = true;
        }
    }

    private bool TryHandleClipboardShortcut(KeyEventArgs e)
    {
        ModifierKeys modifiers = Keyboard.Modifiers;
        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.C)
        {
            CopySelectionToClipboard();
            return true;
        }

        if (modifiers == ModifierKeys.Control && e.Key == Key.Insert)
        {
            CopySelectionToClipboard();
            return true;
        }

        if ((modifiers == ModifierKeys.Control && e.Key == Key.V) ||
            (modifiers == ModifierKeys.Shift && e.Key == Key.Insert) ||
            (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.V))
        {
            PasteFromClipboard();
            return true;
        }

        return false;
    }

    private bool TryHandleControlShortcut(KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control)
        {
            return false;
        }

        if (e.Key == Key.C)
        {
            SendInterrupt();
            return true;
        }

        if (e.Key == Key.Space)
        {
            return SendTerminalInput("\0");
        }

        if (e.Key == Key.Oem4)
        {
            return SendTerminalInput("\u001b");
        }

        if (e.Key >= Key.A && e.Key <= Key.Z)
        {
            char control = (char)(e.Key - Key.A + 1);
            return SendTerminalInput(control.ToString());
        }

        return false;
    }

    private bool TryHandleSpecialKey(KeyEventArgs e)
    {
        ModifierKeys modifiers = Keyboard.Modifiers;
        if (modifiers is not ModifierKeys.None and not ModifierKeys.Shift)
        {
            return false;
        }

        string? sequence = e.Key switch
        {
            Key.Enter => "\r",
            Key.Back => "\b",
            Key.Tab => modifiers == ModifierKeys.Shift ? "\u001b[Z" : "\t",
            Key.Escape => "\u001b",
            Key.Up => _terminalBuffer.ApplicationCursorKeysEnabled ? "\u001bOA" : "\u001b[A",
            Key.Down => _terminalBuffer.ApplicationCursorKeysEnabled ? "\u001bOB" : "\u001b[B",
            Key.Right => _terminalBuffer.ApplicationCursorKeysEnabled ? "\u001bOC" : "\u001b[C",
            Key.Left => _terminalBuffer.ApplicationCursorKeysEnabled ? "\u001bOD" : "\u001b[D",
            Key.Home => _terminalBuffer.ApplicationCursorKeysEnabled ? "\u001bOH" : "\u001b[H",
            Key.End => _terminalBuffer.ApplicationCursorKeysEnabled ? "\u001bOF" : "\u001b[F",
            Key.Insert => "\u001b[2~",
            Key.Delete => "\u001b[3~",
            Key.PageUp => "\u001b[5~",
            Key.PageDown => "\u001b[6~",
            Key.F1 => "\u001bOP",
            Key.F2 => "\u001bOQ",
            Key.F3 => "\u001bOR",
            Key.F4 => "\u001bOS",
            Key.F5 => "\u001b[15~",
            Key.F6 => "\u001b[17~",
            Key.F7 => "\u001b[18~",
            Key.F8 => "\u001b[19~",
            Key.F9 => "\u001b[20~",
            Key.F10 => "\u001b[21~",
            Key.F11 => "\u001b[23~",
            Key.F12 => "\u001b[24~",
            _ => null
        };

        return sequence is not null && SendTerminalInput(sequence);
    }

    private bool TryHandleApplicationKeypad(KeyEventArgs e)
    {
        if (!_terminalBuffer.ApplicationKeypadEnabled || Keyboard.Modifiers != ModifierKeys.None)
        {
            return false;
        }

        string? sequence = e.Key switch
        {
            Key.NumPad0 => "\u001bOp",
            Key.NumPad1 => "\u001bOq",
            Key.NumPad2 => "\u001bOr",
            Key.NumPad3 => "\u001bOs",
            Key.NumPad4 => "\u001bOt",
            Key.NumPad5 => "\u001bOu",
            Key.NumPad6 => "\u001bOv",
            Key.NumPad7 => "\u001bOw",
            Key.NumPad8 => "\u001bOx",
            Key.NumPad9 => "\u001bOy",
            Key.Multiply => "\u001bOj",
            Key.Add => "\u001bOk",
            Key.Separator => "\u001bOl",
            Key.Subtract => "\u001bOm",
            Key.Decimal => "\u001bOn",
            Key.Divide => "\u001bOo",
            _ => null
        };

        return sequence is not null && SendTerminalInput(sequence);
    }

    private void OnTerminalOutputSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var (columns, rows) = CalculateTerminalSize();
        if (columns == _currentColumns && rows == _currentRows)
        {
            return;
        }

        _currentColumns = columns;
        _currentRows = rows;
        _terminalBuffer.Resize(columns, rows);
        RenderTerminal();

        if (_session is null)
        {
            return;
        }

        try
        {
            _session.Resize(columns, rows);
        }
        catch (Exception ex)
        {
            SetStatus($"Resize failed: {ex.Message}");
        }
    }

    private void StartTerminal()
    {
        StopTerminal();

        string commandLine = string.IsNullOrWhiteSpace(CommandTextBox.Text)
            ? BuildDefaultCommandLine()
            : CommandTextBox.Text.Trim();

        (_currentColumns, _currentRows) = CalculateTerminalSize();
        ReplaceTerminalBuffer(new AnsiTerminalBuffer(_currentColumns, _currentRows));
        _cursorBlinkVisible = true;
        RenderTerminal();

        try
        {
            string modeLabel;
            if (_useCompatibilityMode)
            {
                _session = new ProcessPipeSession(commandLine);
                modeLabel = "Compat";
            }
            else
            {
                try
                {
                    _session = new ConPtySession(_currentColumns, _currentRows, commandLine);
                    modeLabel = "ConPTY";
                }
                catch
                {
                    _useCompatibilityMode = true;
                    _session = new ProcessPipeSession(commandLine);
                    modeLabel = "Compat";
                }
            }

            _session.OutputReceived += OnOutputReceived;
            _session.Exited += OnProcessExited;
            _session.Start();
            UpdateUiState(isRunning: true);
            TerminalOutput.Focus();
            SetStatus($"Started ({modeLabel}): {commandLine}");
        }
        catch (Exception ex)
        {
            UpdateUiState(isRunning: false);
            SetStatus($"Failed to start terminal: {FormatExceptionMessage(ex)}");
        }
    }

    private void StopTerminal()
    {
        if (_session is null)
        {
            UpdateUiState(isRunning: false);
            UpdateWindowTitle();
            return;
        }

        _session.OutputReceived -= OnOutputReceived;
        _session.Exited -= OnProcessExited;
        _session.Dispose();
        _session = null;

        UpdateUiState(isRunning: false);
        UpdateWindowTitle();
        SetStatus("Stopped.");
    }

    private void OnOutputReceived(object? sender, string text)
    {
        Dispatcher.Invoke(() =>
        {
            _terminalBuffer.Process(text);
            RenderTerminal();
        });
    }

    private void OnProcessExited(object? sender, int exitCode)
    {
        Dispatcher.Invoke(() =>
        {
            SetStatus($"Process exited with code {exitCode}.");
            StopTerminal();
        });
    }

    private void CursorBlinkTimer_Tick(object? sender, EventArgs e)
    {
        bool nextVisible = TerminalOutput.IsKeyboardFocusWithin && _session is not null
            ? !_cursorBlinkVisible
            : false;

        if (_cursorBlinkVisible == nextVisible)
        {
            return;
        }

        _cursorBlinkVisible = nextVisible;
        RenderTerminal();
    }

    private bool SendTerminalInput(string text)
    {
        if (_session is null || string.IsNullOrEmpty(text))
        {
            return false;
        }

        try
        {
            _session.Write(text);
            _cursorBlinkVisible = true;
            return true;
        }
        catch (Exception ex)
        {
            SetStatus($"Send failed: {ex.Message}");
            return false;
        }
    }

    private void SendInterrupt()
    {
        _ = SendTerminalInput("\u0003");
    }

    private void PasteFromClipboard()
    {
        if (_session is null || !Clipboard.ContainsText())
        {
            return;
        }

        string text = Clipboard.GetText();
        if (_terminalBuffer.BracketedPasteEnabled)
        {
            text = $"\u001b[200~{text}\u001b[201~";
        }

        _ = SendTerminalInput(text);
    }

    private void CopySelectionToClipboard()
    {
        TextSelection selection = TerminalOutput.Selection;
        if (selection.IsEmpty)
        {
            return;
        }

        string text = new TextRange(selection.Start, selection.End).Text;
        if (!string.IsNullOrEmpty(text))
        {
            Clipboard.SetText(text);
            SetStatus("Copied selection.");
        }
    }

    private void UpdateUiState(bool isRunning)
    {
        StartButton.IsEnabled = !isRunning;
        StopButton.IsEnabled = isRunning;
        RecoverButton.IsEnabled = isRunning;
        PasteButton.IsEnabled = isRunning;
        InterruptButton.IsEnabled = isRunning;
        CommandTextBox.IsEnabled = !isRunning;
    }

    private void RenderTerminal()
    {
        if (_isRenderingTerminal)
        {
            return;
        }

        bool shouldRestoreFocus = TerminalOutput.IsKeyboardFocusWithin;
        _isRenderingTerminal = true;
        try
        {
            TerminalOutput.Document = _terminalBuffer.CreateDocument(
                TerminalOutput.FontFamily,
                TerminalOutput.FontSize,
                showCursor: ShouldShowCursor());
            if (shouldRestoreFocus && !TerminalOutput.IsKeyboardFocusWithin)
            {
                Keyboard.Focus(TerminalOutput);
            }

            TerminalOutput.ScrollToEnd();
            UpdateWindowTitle();
        }
        finally
        {
            _isRenderingTerminal = false;
        }
    }

    private bool ShouldShowCursor()
    {
        return _session is not null && TerminalOutput.IsKeyboardFocusWithin && _cursorBlinkVisible;
    }

    private void ReplaceTerminalBuffer(AnsiTerminalBuffer nextBuffer)
    {
        _terminalBuffer.InputSequenceGenerated -= TerminalBuffer_InputSequenceGenerated;
        _terminalBuffer = nextBuffer;
        _terminalBuffer.InputSequenceGenerated += TerminalBuffer_InputSequenceGenerated;
    }

    private void TerminalBuffer_InputSequenceGenerated(object? sender, string text)
    {
        if (_session is null || string.IsNullOrEmpty(text))
        {
            return;
        }

        try
        {
            _session.Write(text);
        }
        catch (Exception ex)
        {
            SetStatus($"Terminal response failed: {ex.Message}");
        }
    }

    private void EmitFocusReport(bool focused)
    {
        if (!_terminalBuffer.FocusReportingEnabled)
        {
            return;
        }

        _ = SendTerminalInput(focused ? "\u001b[I" : "\u001b[O");
    }

    private bool TrySendMouseButtonEvent(MouseButtonEventArgs e, bool pressed)
    {
        if (_session is null || _terminalBuffer.MouseTrackingMode == TerminalMouseTrackingMode.Off)
        {
            return false;
        }

        if (!TryGetMouseCell(e.GetPosition(TerminalOutput), out int column, out int row))
        {
            return false;
        }

        if (_terminalBuffer.MouseTrackingMode == TerminalMouseTrackingMode.X10 && !pressed)
        {
            return false;
        }

        int? button = pressed ? MapMouseButton(e.ChangedButton) : 3;
        if (!button.HasValue)
        {
            return false;
        }

        string sequence = EncodeMouseSequence(button.Value, column, row, released: !pressed, motion: false, wheel: false, wheelUp: false);
        return SendTerminalInput(sequence);
    }

    private bool TrySendMouseMoveEvent(MouseEventArgs e)
    {
        if (_session is null)
        {
            return false;
        }

        TerminalMouseTrackingMode mode = _terminalBuffer.MouseTrackingMode;
        if (mode is TerminalMouseTrackingMode.Off or TerminalMouseTrackingMode.X10)
        {
            return false;
        }

        bool hasPressedButton =
            e.LeftButton == MouseButtonState.Pressed ||
            e.MiddleButton == MouseButtonState.Pressed ||
            e.RightButton == MouseButtonState.Pressed;
        if (mode == TerminalMouseTrackingMode.ButtonEvent && !hasPressedButton)
        {
            return false;
        }

        if (!TryGetMouseCell(e.GetPosition(TerminalOutput), out int column, out int row))
        {
            return false;
        }

        int button = ResolveCurrentMouseButton(e);
        string sequence = EncodeMouseSequence(button, column, row, released: false, motion: true, wheel: false, wheelUp: false);
        return SendTerminalInput(sequence);
    }

    private bool TrySendMouseWheelEvent(MouseWheelEventArgs e)
    {
        if (_session is null || _terminalBuffer.MouseTrackingMode == TerminalMouseTrackingMode.Off)
        {
            return false;
        }

        if (!TryGetMouseCell(e.GetPosition(TerminalOutput), out int column, out int row))
        {
            return false;
        }

        bool wheelUp = e.Delta > 0;
        string sequence = EncodeMouseSequence(0, column, row, released: false, motion: false, wheel: true, wheelUp: wheelUp);
        return SendTerminalInput(sequence);
    }

    private string EncodeMouseSequence(int button, int column, int row, bool released, bool motion, bool wheel, bool wheelUp)
    {
        int code = button;
        if (wheel)
        {
            code = wheelUp ? 64 : 65;
        }
        else
        {
            if (motion)
            {
                code += 32;
            }

            if (released)
            {
                code = 3;
            }
        }

        code += GetMouseModifierBits();

        if (_terminalBuffer.UseSgrMouseEncoding)
        {
            char suffix = released && !motion && !wheel ? 'm' : 'M';
            return $"\u001b[<{code};{column};{row}{suffix}";
        }

        int encodedButton = Math.Clamp(code + 32, 32, 127);
        int encodedColumn = Math.Clamp(column + 32, 32, 127);
        int encodedRow = Math.Clamp(row + 32, 32, 127);
        return $"\u001b[M{(char)encodedButton}{(char)encodedColumn}{(char)encodedRow}";
    }

    private static int? MapMouseButton(MouseButton button)
    {
        return button switch
        {
            MouseButton.Left => 0,
            MouseButton.Middle => 1,
            MouseButton.Right => 2,
            _ => null
        };
    }

    private static int ResolveCurrentMouseButton(MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            return 0;
        }

        if (e.MiddleButton == MouseButtonState.Pressed)
        {
            return 1;
        }

        if (e.RightButton == MouseButtonState.Pressed)
        {
            return 2;
        }

        return 3;
    }

    private static int GetMouseModifierBits()
    {
        ModifierKeys modifiers = Keyboard.Modifiers;
        int bits = 0;
        if ((modifiers & ModifierKeys.Shift) != 0)
        {
            bits += 4;
        }

        if ((modifiers & ModifierKeys.Alt) != 0)
        {
            bits += 8;
        }

        if ((modifiers & ModifierKeys.Control) != 0)
        {
            bits += 16;
        }

        return bits;
    }

    private bool TryGetMouseCell(Point position, out int column, out int row)
    {
        var (charWidth, charHeight) = MeasureCharacterCell();
        double x = Math.Max(0, position.X - TerminalOutput.Padding.Left);
        double y = Math.Max(0, position.Y - TerminalOutput.Padding.Top);
        column = Math.Clamp((int)(x / charWidth) + 1, 1, _currentColumns);
        row = Math.Clamp((int)(y / charHeight) + 1, 1, _currentRows);
        return true;
    }

    private (double Width, double Height) MeasureCharacterCell()
    {
        var typeface = new Typeface(
            TerminalOutput.FontFamily,
            TerminalOutput.FontStyle,
            TerminalOutput.FontWeight,
            TerminalOutput.FontStretch);

        var sample = new FormattedText(
            "W",
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            TerminalOutput.FontSize,
            Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        return (
            Math.Max(sample.WidthIncludingTrailingWhitespace, 1.0),
            Math.Max(sample.Height, 1.0));
    }

    private void UpdateWindowTitle()
    {
        string terminalTitle = _terminalBuffer.WindowTitle;
        Title = string.IsNullOrWhiteSpace(terminalTitle)
            ? BaseWindowTitle
            : $"{terminalTitle} - {BaseWindowTitle}";
    }

    private void SetStatus(string message)
    {
        StatusText.Text = $"[{DateTime.Now:HH:mm:ss}] {message}";
    }

    private static string BuildDefaultCommandLine()
    {
        string? comSpec = Environment.GetEnvironmentVariable("ComSpec");
        if (!string.IsNullOrWhiteSpace(comSpec) && File.Exists(comSpec))
        {
            return $"\"{comSpec}\" /K";
        }

        return "cmd.exe /K";
    }

    private static string FormatExceptionMessage(Exception ex)
    {
        if (ex is Win32Exception win32)
        {
            return $"{win32.Message} (Win32={win32.NativeErrorCode}, HRESULT=0x{win32.HResult:X8})";
        }

        return $"{ex.Message} (HRESULT=0x{ex.HResult:X8})";
    }

    private void SessionWatchdog_Tick(object? sender, EventArgs e)
    {
        if (_session is null || _isRecovering)
        {
            return;
        }

        if (!_session.IsOutputStalled(InitialOutputTimeout, IdleOutputTimeout))
        {
            return;
        }

        if (_autoRecoveryAttempts >= MaxAutoRecoveryAttempts)
        {
            SetStatus(_useCompatibilityMode
                ? "Output stalled in compatibility mode. Click Recover."
                : "Output stalled. Switching to compatibility mode via Recover.");
            return;
        }

        _autoRecoveryAttempts++;
        RecoverSession(isAutomatic: true);
    }

    private void RecoverSession(bool isAutomatic)
    {
        if (_session is null || _isRecovering)
        {
            return;
        }

        _isRecovering = true;
        try
        {
            _session.TryForceUnlock();
            if (!_useCompatibilityMode)
            {
                _useCompatibilityMode = true;
            }

            SetStatus(isAutomatic
                ? "Output stalled. Unlocking and restarting in compatibility mode..."
                : "Recover requested. Unlocking and restarting in compatibility mode...");
            StartTerminal();
        }
        catch (Exception ex)
        {
            SetStatus($"Recovery failed: {FormatExceptionMessage(ex)}");
        }
        finally
        {
            _isRecovering = false;
        }
    }

    private (short Columns, short Rows) CalculateTerminalSize()
    {
        if (TerminalOutput.ActualWidth <= 0 || TerminalOutput.ActualHeight <= 0)
        {
            return (120, 30);
        }

        var typeface = new Typeface(
            TerminalOutput.FontFamily,
            TerminalOutput.FontStyle,
            TerminalOutput.FontWeight,
            TerminalOutput.FontStretch);

        var sample = new FormattedText(
            "W",
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            TerminalOutput.FontSize,
            Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        double charWidth = Math.Max(sample.WidthIncludingTrailingWhitespace, 1.0);
        double charHeight = Math.Max(sample.Height, 1.0);

        int columns = Math.Clamp((int)(TerminalOutput.ActualWidth / charWidth), 20, 500);
        int rows = Math.Clamp((int)(TerminalOutput.ActualHeight / charHeight), 10, 300);

        return ((short)columns, (short)rows);
    }
}
