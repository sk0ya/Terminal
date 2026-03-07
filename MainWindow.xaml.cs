using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;

namespace ConPtyTerminal;

public partial class MainWindow : Window
{
    private const string BaseWindowTitle = "ConPTY Terminal";
    private const int MaxAutoRecoveryAttempts = 1;
    private const double AutoFollowThreshold = 2.0;

    private static readonly TimeSpan InitialOutputTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan IdleOutputTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CursorBlinkInterval = TimeSpan.FromMilliseconds(530);
    private static readonly Brush BlockCursorBrush = CreateFrozenBrush(Color.FromArgb(0xA0, 0xE3, 0xE3, 0xE3));
    private static readonly Brush AccentCursorBrush = CreateFrozenBrush(Color.FromRgb(0x5F, 0xAF, 0xFF));

    private ITerminalSession? _session;
    private AnsiTerminalBuffer _terminalBuffer = new(120, 30);
    private short _currentColumns = 120;
    private short _currentRows = 30;
    private readonly DispatcherTimer _sessionWatchdog = new();
    private readonly DispatcherTimer _cursorBlinkTimer = new();
    private readonly object _pendingOutputLock = new();
    private readonly StringBuilder _pendingOutput = new();
    private ScrollViewer? _terminalScrollViewer;
    private int _autoRecoveryAttempts;
    private bool _isRecovering;
    private bool _isImeComposing;
    private bool _isRenderingTerminal;
    private bool _documentRenderScheduled;
    private bool _outputFlushScheduled;
    private bool _followTerminalOutput = true;
    private bool _useCompatibilityMode;
    private bool _cursorBlinkVisible = true;
    private string _imeCompositionText = string.Empty;

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

        TextCompositionManager.AddPreviewTextInputStartHandler(TerminalInputProxy, TerminalInputProxy_PreviewTextInputStart);
        TextCompositionManager.AddPreviewTextInputUpdateHandler(TerminalInputProxy, TerminalInputProxy_PreviewTextInputUpdate);
        TerminalOutput.AddHandler(Hyperlink.RequestNavigateEvent, new RequestNavigateEventHandler(TerminalOutput_RequestNavigate));

        Loaded += OnLoaded;
        Closing += OnClosing;
        Activated += OnActivated;
        Deactivated += OnDeactivated;
        TerminalOutput.SizeChanged += OnTerminalOutputSizeChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachTerminalScrollViewer();
        UpdateInputProxyPosition();
        RequestDocumentRender();
        StartTerminal();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _sessionWatchdog.Stop();
        _cursorBlinkTimer.Stop();
        ClearImeComposition();
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

    private void TerminalOutput_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri)
            {
                UseShellExecute = true
            });
            e.Handled = true;
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to open link: {ex.Message}");
        }
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
        RequestDocumentRender();
        SetStatus("Cleared local scrollback.");
    }

    private void TerminalOutput_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (TrySendMouseButtonEvent(e, pressed: true))
        {
            e.Handled = true;
        }
    }

    private void TerminalOutput_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        QueueTerminalInputFocus();

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
        QueueTerminalInputFocus();
        UpdateTerminalFocusState(focused: true);
    }

    private void TerminalOutput_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (TerminalInputProxy.IsKeyboardFocusWithin)
        {
            return;
        }

        UpdateTerminalFocusState(focused: false);
    }

    private void TerminalInputProxy_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        UpdateInputProxyPosition();
        UpdateTerminalFocusState(focused: true);
    }

    private void TerminalInputProxy_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (TerminalOutput.IsKeyboardFocusWithin)
        {
            return;
        }

        ClearImeComposition();
        UpdateTerminalFocusState(focused: false);
    }

    private void TerminalInputProxy_PreviewTextInputStart(object sender, TextCompositionEventArgs e)
    {
        UpdateImeComposition(e.TextComposition.CompositionText);
    }

    private void TerminalInputProxy_PreviewTextInputUpdate(object sender, TextCompositionEventArgs e)
    {
        UpdateImeComposition(e.TextComposition.CompositionText);
    }

    private void TerminalOutput_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        string text = string.IsNullOrEmpty(e.Text) ? e.SystemText : e.Text;
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        ModifierKeys modifiers = GetTerminalModifiers();
        if ((modifiers & ModifierKeys.Alt) != 0 && (modifiers & ModifierKeys.Control) == 0)
        {
            text = $"\u001b{text}";
        }

        if (SendTerminalInput(text))
        {
            ClearImeComposition();
            ResetInputProxyText();
            e.Handled = true;
        }
    }

    private void TerminalOutput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_session is null)
        {
            return;
        }

        if (TryHandleClipboardShortcut(e))
        {
            e.Handled = true;
            return;
        }

        if (IsImeInputInProgress(e))
        {
            return;
        }

        if (TryHandleControlShortcut(e) || TryHandleApplicationKeypad(e) || TryHandleSpecialKey(e))
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

    private bool IsImeInputInProgress(KeyEventArgs e)
    {
        return _isImeComposing ||
            e.ImeProcessedKey != Key.None ||
            e.Key == Key.ImeProcessed ||
            e.Key == Key.ImeConvert ||
            e.Key == Key.ImeNonConvert ||
            e.Key == Key.ImeAccept ||
            e.Key == Key.ImeModeChange;
    }

    private bool TryHandleControlShortcut(KeyEventArgs e)
    {
        if (GetTerminalModifiers() != ModifierKeys.Control)
        {
            return false;
        }

        string? chord = TerminalKeyChordTranslator.TranslateCtrlChord(e.Key);
        if (chord is null)
        {
            return false;
        }

        if (e.Key == Key.C)
        {
            SendInterrupt();
            return true;
        }

        return SendTerminalInput(chord);
    }

    private bool TryHandleSpecialKey(KeyEventArgs e)
    {
        ModifierKeys modifiers = GetTerminalModifiers();
        string? sequence = TerminalKeyChordTranslator.TranslateSpecialKey(
            e.Key,
            modifiers,
            _terminalBuffer.ApplicationCursorKeysEnabled);

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
        RequestDocumentRender();

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
        RequestDocumentRender();

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
            FocusTerminalInput();
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
            ClearPendingOutput();
            ClearImeComposition();
            ResetInputProxyText();
            UpdateOverlayState();
            UpdateUiState(isRunning: false);
            UpdateWindowTitle();
            return;
        }

        _session.OutputReceived -= OnOutputReceived;
        _session.Exited -= OnProcessExited;
        _session.Dispose();
        _session = null;

        ClearPendingOutput();
        ClearImeComposition();
        ResetInputProxyText();
        UpdateOverlayState();
        UpdateUiState(isRunning: false);
        UpdateWindowTitle();
        SetStatus("Stopped.");
    }

    private void OnOutputReceived(object? sender, string text)
    {
        QueueTerminalOutput(text);
    }

    private void OnProcessExited(object? sender, int exitCode)
    {
        Dispatcher.Invoke(() =>
        {
            FlushPendingOutput();
            SetStatus($"Process exited with code {exitCode}.");
            StopTerminal();
        });
    }

    private void CursorBlinkTimer_Tick(object? sender, EventArgs e)
    {
        bool nextVisible;
        if (_session is null || !HasTerminalInputFocus())
        {
            nextVisible = false;
        }
        else if (!_terminalBuffer.CursorBlinkEnabled)
        {
            nextVisible = true;
        }
        else
        {
            nextVisible = !_cursorBlinkVisible;
        }

        if (_cursorBlinkVisible == nextVisible)
        {
            return;
        }

        _cursorBlinkVisible = nextVisible;
        UpdateOverlayState();
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

    private void QueueTerminalOutput(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        bool shouldSchedule = false;
        lock (_pendingOutputLock)
        {
            _pendingOutput.Append(text);
            if (!_outputFlushScheduled)
            {
                _outputFlushScheduled = true;
                shouldSchedule = true;
            }
        }

        if (shouldSchedule)
        {
            _ = Dispatcher.BeginInvoke(FlushPendingOutput, DispatcherPriority.Background);
        }
    }

    private void FlushPendingOutput()
    {
        string? nextBatch = null;
        lock (_pendingOutputLock)
        {
            if (_pendingOutput.Length > 0)
            {
                nextBatch = _pendingOutput.ToString();
                _pendingOutput.Clear();
            }

            _outputFlushScheduled = false;
        }

        if (!string.IsNullOrEmpty(nextBatch))
        {
            _terminalBuffer.Process(nextBatch);
            RequestDocumentRender();
        }

        bool shouldReschedule = false;
        lock (_pendingOutputLock)
        {
            if (_pendingOutput.Length > 0 && !_outputFlushScheduled)
            {
                _outputFlushScheduled = true;
                shouldReschedule = true;
            }
        }

        if (shouldReschedule)
        {
            _ = Dispatcher.BeginInvoke(FlushPendingOutput, DispatcherPriority.Background);
        }
    }

    private void ClearPendingOutput()
    {
        lock (_pendingOutputLock)
        {
            _pendingOutput.Clear();
            _outputFlushScheduled = false;
        }
    }

    private bool SendTerminalInput(byte[] bytes)
    {
        if (_session is null || bytes.Length == 0)
        {
            return false;
        }

        try
        {
            _session.Write(bytes);
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

    private void RequestDocumentRender()
    {
        if (_documentRenderScheduled)
        {
            return;
        }

        _documentRenderScheduled = true;
        _ = Dispatcher.BeginInvoke(PerformDocumentRender, DispatcherPriority.Background);
    }

    private void PerformDocumentRender()
    {
        _documentRenderScheduled = false;
        RenderTerminal();
    }

    private void RenderTerminal()
    {
        if (_isRenderingTerminal)
        {
            RequestDocumentRender();
            return;
        }

        AttachTerminalScrollViewer();
        bool shouldRestoreFocus = HasTerminalInputFocus();
        double preservedDistanceFromBottom = GetDistanceFromBottom();
        _isRenderingTerminal = true;
        try
        {
            TerminalOutput.Document = _terminalBuffer.CreateDocument(
                TerminalOutput.FontFamily,
                TerminalOutput.FontSize,
                showCursor: false);
            TerminalOutput.UpdateLayout();
            UpdateInputProxyPosition();
            if (shouldRestoreFocus && !HasTerminalInputFocus())
            {
                FocusTerminalInput();
            }

            RestoreTerminalViewport(preservedDistanceFromBottom);
            UpdateWindowTitle();
        }
        finally
        {
            _isRenderingTerminal = false;
        }
    }

    private bool ShouldShowCursor()
    {
        return _session is not null &&
            HasTerminalInputFocus() &&
            (!_terminalBuffer.CursorBlinkEnabled || _cursorBlinkVisible);
    }

    private bool HasTerminalInputFocus()
    {
        return TerminalInputProxy.IsKeyboardFocusWithin || TerminalOutput.IsKeyboardFocusWithin;
    }

    private void UpdateTerminalFocusState(bool focused)
    {
        _cursorBlinkVisible = focused || !_terminalBuffer.CursorBlinkEnabled;
        if (_isRenderingTerminal)
        {
            return;
        }

        UpdateOverlayState();
    }

    private void QueueTerminalInputFocus()
    {
        if (_session is null)
        {
            return;
        }

        Dispatcher.BeginInvoke(FocusTerminalInput, DispatcherPriority.Input);
    }

    private void FocusTerminalInput()
    {
        if (_session is null)
        {
            return;
        }

        ResetInputProxyText();
        UpdateInputProxyPosition();
        Keyboard.Focus(TerminalInputProxy);
    }

    private void UpdateInputProxyPosition()
    {
        var (charWidth, charHeight) = MeasureCharacterCell();
        TerminalInputProxy.Width = Math.Max(2, charWidth);
        TerminalInputProxy.Height = Math.Max(2, charHeight);
        TerminalInputProxy.FontFamily = TerminalOutput.FontFamily;
        TerminalInputProxy.FontSize = TerminalOutput.FontSize;

        double left = TerminalOutput.Padding.Left + (_terminalBuffer.CursorColumn * charWidth);
        double top = TerminalOutput.Padding.Top + (_terminalBuffer.CursorRow * charHeight);
        double maxLeft = Math.Max(TerminalOutput.Padding.Left, TerminalOutput.ActualWidth - TerminalOutput.Padding.Right - TerminalInputProxy.Width);
        double maxTop = Math.Max(TerminalOutput.Padding.Top, TerminalOutput.ActualHeight - TerminalOutput.Padding.Bottom - TerminalInputProxy.Height);

        Canvas.SetLeft(TerminalInputProxy, Math.Clamp(left, TerminalOutput.Padding.Left, maxLeft));
        Canvas.SetTop(TerminalInputProxy, Math.Clamp(top, TerminalOutput.Padding.Top, maxTop));
        UpdateCursorOverlay(left, top, charWidth, charHeight, maxLeft, maxTop);
        UpdateImeCompositionOverlay(left, top, charHeight, maxLeft, maxTop);
    }

    private void ResetInputProxyText()
    {
        if (TerminalInputProxy.Text.Length == 0)
        {
            return;
        }

        TerminalInputProxy.Clear();
    }

    private void UpdateImeComposition(string? compositionText)
    {
        _imeCompositionText = compositionText ?? string.Empty;
        _isImeComposing = _imeCompositionText.Length > 0;
        UpdateInputProxyPosition();
    }

    private void ClearImeComposition()
    {
        _imeCompositionText = string.Empty;
        _isImeComposing = false;
        ImeCompositionOverlay.Visibility = Visibility.Collapsed;
        ImeCompositionTextBlock.Text = string.Empty;
        if (!_isRenderingTerminal)
        {
            UpdateOverlayState();
        }
    }

    private void UpdateImeCompositionOverlay(double left, double top, double charHeight, double maxLeft, double maxTop)
    {
        if (!_isImeComposing || string.IsNullOrEmpty(_imeCompositionText) || !HasTerminalInputFocus())
        {
            ImeCompositionOverlay.Visibility = Visibility.Collapsed;
            ImeCompositionTextBlock.Text = string.Empty;
            return;
        }

        ImeCompositionTextBlock.FontFamily = TerminalOutput.FontFamily;
        ImeCompositionTextBlock.FontSize = TerminalOutput.FontSize;
        ImeCompositionTextBlock.Text = _imeCompositionText;

        Size textSize = MeasureTerminalText(_imeCompositionText);
        double overlayWidth = Math.Max(textSize.Width + 4, 8);
        double overlayHeight = Math.Max(textSize.Height, charHeight);
        double overlayLeft = Math.Clamp(left, TerminalOutput.Padding.Left, Math.Max(TerminalOutput.Padding.Left, maxLeft - overlayWidth + TerminalInputProxy.Width));
        double overlayTop = Math.Clamp(top, TerminalOutput.Padding.Top, Math.Max(TerminalOutput.Padding.Top, maxTop - overlayHeight));

        ImeCompositionOverlay.Width = overlayWidth;
        ImeCompositionOverlay.Height = overlayHeight;
        Canvas.SetLeft(ImeCompositionOverlay, overlayLeft);
        Canvas.SetTop(ImeCompositionOverlay, overlayTop);
        ImeCompositionOverlay.Visibility = Visibility.Visible;
    }

    private void UpdateCursorOverlay(double left, double top, double charWidth, double charHeight, double maxLeft, double maxTop)
    {
        if (!ShouldShowCursorOverlay())
        {
            TerminalCursorOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        double overlayWidth = Math.Max(2, Math.Ceiling(charWidth));
        double overlayHeight = Math.Max(2, Math.Ceiling(charHeight));
        Brush background = BlockCursorBrush;

        switch (_terminalBuffer.CursorShape)
        {
            case TerminalCursorShape.Underline:
                overlayHeight = Math.Max(2, Math.Ceiling(charHeight / 6));
                top += Math.Max(0, charHeight - overlayHeight);
                background = AccentCursorBrush;
                break;
            case TerminalCursorShape.Bar:
                overlayWidth = Math.Max(2, Math.Ceiling(charWidth / 6));
                background = AccentCursorBrush;
                break;
        }

        double overlayLeft = Math.Clamp(left, TerminalOutput.Padding.Left, Math.Max(TerminalOutput.Padding.Left, maxLeft - overlayWidth + TerminalInputProxy.Width));
        double overlayTop = Math.Clamp(top, TerminalOutput.Padding.Top, Math.Max(TerminalOutput.Padding.Top, maxTop - overlayHeight));

        TerminalCursorOverlay.Width = overlayWidth;
        TerminalCursorOverlay.Height = overlayHeight;
        TerminalCursorOverlay.Background = background;
        Canvas.SetLeft(TerminalCursorOverlay, overlayLeft);
        Canvas.SetTop(TerminalCursorOverlay, overlayTop);
        TerminalCursorOverlay.Visibility = Visibility.Visible;
    }

    private bool ShouldShowCursorOverlay()
    {
        return ShouldShowCursor() && _terminalBuffer.CursorVisible && !_isImeComposing;
    }

    private void UpdateOverlayState()
    {
        if (_isRenderingTerminal)
        {
            return;
        }

        UpdateInputProxyPosition();
    }

    private void AttachTerminalScrollViewer()
    {
        if (_terminalScrollViewer is not null)
        {
            return;
        }

        TerminalOutput.ApplyTemplate();
        TerminalOutput.UpdateLayout();
        _terminalScrollViewer = FindDescendant<ScrollViewer>(TerminalOutput);
        if (_terminalScrollViewer is null)
        {
            return;
        }

        _terminalScrollViewer.ScrollChanged += TerminalScrollViewer_ScrollChanged;
        UpdateFollowOutputState();
    }

    private void TerminalScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_isRenderingTerminal)
        {
            return;
        }

        UpdateFollowOutputState();
    }

    private double GetDistanceFromBottom()
    {
        if (_terminalScrollViewer is null)
        {
            return 0;
        }

        return Math.Max(
            0,
            _terminalScrollViewer.ExtentHeight - _terminalScrollViewer.VerticalOffset - _terminalScrollViewer.ViewportHeight);
    }

    private void RestoreTerminalViewport(double preservedDistanceFromBottom)
    {
        if (_terminalScrollViewer is null)
        {
            if (_followTerminalOutput)
            {
                TerminalOutput.ScrollToEnd();
            }

            return;
        }

        if (_followTerminalOutput || preservedDistanceFromBottom <= AutoFollowThreshold)
        {
            _terminalScrollViewer.ScrollToBottom();
        }
        else
        {
            double targetOffset = Math.Max(
                0,
                _terminalScrollViewer.ExtentHeight - _terminalScrollViewer.ViewportHeight - preservedDistanceFromBottom);
            _terminalScrollViewer.ScrollToVerticalOffset(targetOffset);
        }

        UpdateFollowOutputState();
    }

    private void UpdateFollowOutputState()
    {
        _followTerminalOutput = _terminalScrollViewer is null || GetDistanceFromBottom() <= AutoFollowThreshold;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < childCount; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }

            T? nested = FindDescendant<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private void ReplaceTerminalBuffer(AnsiTerminalBuffer nextBuffer)
    {
        _terminalBuffer.InputSequenceGenerated -= TerminalBuffer_InputSequenceGenerated;
        _terminalBuffer.ClipboardSetRequested -= TerminalBuffer_ClipboardSetRequested;
        _terminalBuffer.ClipboardQueryRequested -= TerminalBuffer_ClipboardQueryRequested;
        _terminalBuffer = nextBuffer;
        _terminalBuffer.InputSequenceGenerated += TerminalBuffer_InputSequenceGenerated;
        _terminalBuffer.ClipboardSetRequested += TerminalBuffer_ClipboardSetRequested;
        _terminalBuffer.ClipboardQueryRequested += TerminalBuffer_ClipboardQueryRequested;
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

    private void TerminalBuffer_ClipboardSetRequested(object? sender, string text)
    {
        try
        {
            Clipboard.SetText(text ?? string.Empty);
            SetStatus("Clipboard updated by terminal.");
        }
        catch (Exception ex)
        {
            SetStatus($"Clipboard update failed: {ex.Message}");
        }
    }

    private void TerminalBuffer_ClipboardQueryRequested(object? sender, string selectionTargets)
    {
        if (_session is null)
        {
            return;
        }

        try
        {
            string text = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
            string encodedText = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text));
            _session.Write($"\u001b]52;{NormalizeClipboardSelectionTargets(selectionTargets)};{encodedText}\u0007");
        }
        catch (Exception ex)
        {
            SetStatus($"Clipboard query failed: {ex.Message}");
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

        return SendMouseSequence(button.Value, column, row, released: !pressed, motion: false, wheel: false, wheelUp: false);
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
        return SendMouseSequence(button, column, row, released: false, motion: true, wheel: false, wheelUp: false);
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
        return SendMouseSequence(0, column, row, released: false, motion: false, wheel: true, wheelUp: wheelUp);
    }

    private bool SendMouseSequence(int button, int column, int row, bool released, bool motion, bool wheel, bool wheelUp)
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

        code += TerminalInputEncoder.GetMouseModifierBits(GetTerminalModifiers());

        bool sgrRelease = released && !motion && !wheel;
        byte[] sequence = TerminalInputEncoder.EncodeMouseSequence(_terminalBuffer.MouseEncoding, code, column, row, sgrRelease);
        return SendTerminalInput(sequence);
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

    private static ModifierKeys GetTerminalModifiers()
    {
        return Keyboard.Modifiers & (ModifierKeys.Shift | ModifierKeys.Control | ModifierKeys.Alt);
    }

    private static string NormalizeClipboardSelectionTargets(string? selectionTargets)
    {
        return string.IsNullOrWhiteSpace(selectionTargets) ? "c" : selectionTargets.Trim();
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
        Size size = MeasureTerminalText("W");
        return (
            Math.Max(size.Width, 1.0),
            Math.Max(size.Height, 1.0));
    }

    private Size MeasureTerminalText(string text)
    {
        var typeface = new Typeface(
            TerminalOutput.FontFamily,
            TerminalOutput.FontStyle,
            TerminalOutput.FontWeight,
            TerminalOutput.FontStretch);

        var sample = new FormattedText(
            string.IsNullOrEmpty(text) ? " " : text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            TerminalOutput.FontSize,
            Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        return new Size(
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

    private static Brush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
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
