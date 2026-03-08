using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Terminal;

public partial class TerminalTabView : UserControl
{
    private const int MaxAutoRecoveryAttempts = 1;
    private const double AutoFollowThreshold = 2.0;

    private static readonly TimeSpan InitialOutputTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan IdleOutputTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CursorBlinkInterval = TimeSpan.FromMilliseconds(530);
    private static readonly TimeSpan MinDocumentRenderInterval = TimeSpan.FromMilliseconds(33);
    private static readonly TimeSpan CloseShutdownTimeout = TimeSpan.FromSeconds(2);
    private static readonly Brush BlockCursorBrush = CreateFrozenBrush(Color.FromArgb(0xA0, 0xE3, 0xE3, 0xE3));
    private static readonly Brush AccentCursorBrush = CreateFrozenBrush(Color.FromRgb(0x5F, 0xAF, 0xFF));

    private ITerminalSession? _session;
    private AnsiTerminalBuffer _terminalBuffer = new(120, 30);
    private short _currentColumns = 120;
    private short _currentRows = 30;
    private readonly DispatcherTimer _sessionWatchdog = new(DispatcherPriority.Background);
    private readonly DispatcherTimer _cursorBlinkTimer = new(DispatcherPriority.Background);
    private readonly DispatcherTimer _renderThrottleTimer = new(DispatcherPriority.Background);
    private readonly SemaphoreSlim _sessionLifecycleGate = new(1, 1);
    private readonly object _pendingOutputLock = new();
    private readonly StringBuilder _pendingOutput = new();
    private int _autoRecoveryAttempts;
    private bool _isRecovering;
    private bool _isSessionTransitionActive;
    private bool _isClosingWindow;
    private bool _isRenderingTerminal;
    private bool _documentRenderScheduled;
    private bool _outputFlushScheduled;
    private bool _prioritizeInitialOutputRender;
    private bool _followTerminalOutput = true;
    private bool _useCompatibilityMode;
    private bool _cursorBlinkVisible = true;
    private bool _reportedUnsupportedResize;
    private bool _resettingInputProxyText;
    private bool _pendingProxyFlushAfterImeConfirm;
    private bool _terminalMouseCaptureActive;
    private DateTime _lastDocumentRenderUtc = DateTime.MinValue;
    private readonly string _initialCommandLine;
    private readonly string _initialWorkingDirectory;
    private bool _hasStartedInitialSession;

    public event EventHandler<string>? HeaderTitleChanged;

    public string HeaderTitle { get; private set; } = "Terminal";

    public TerminalTabView(string? commandLine = null, string? workingDirectory = null)
    {
        _initialCommandLine = string.IsNullOrWhiteSpace(commandLine)
            ? TerminalProfileCatalog.BuildDefaultCommandLine()
            : commandLine.Trim();
        _initialWorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
            ? Environment.CurrentDirectory
            : workingDirectory.Trim();

        InitializeComponent();
        InputMethod.SetIsInputMethodEnabled(TerminalOutput, false);
        InputMethod.SetIsInputMethodSuspended(TerminalOutput, true);
        InitializeTerminalWorkbench();
        CommandTextBox.Text = _initialCommandLine;
        WorkingDirectoryTextBox.Text = _initialWorkingDirectory;
        UpdateWindowTitle();

        _sessionWatchdog.Interval = TimeSpan.FromSeconds(1);
        _sessionWatchdog.Tick += SessionWatchdog_Tick;
        _sessionWatchdog.Start();

        _cursorBlinkTimer.Interval = CursorBlinkInterval;
        _cursorBlinkTimer.Tick += CursorBlinkTimer_Tick;
        _cursorBlinkTimer.Start();

        _renderThrottleTimer.Tick += RenderThrottleTimer_Tick;

        TerminalOutput.HyperlinkActivated += TerminalOutput_HyperlinkActivated;
        TerminalInputProxy.AddHandler(TextCompositionManager.PreviewTextInputStartEvent, new TextCompositionEventHandler(TerminalInputProxy_PreviewTextInputStart), handledEventsToo: true);
        TerminalInputProxy.AddHandler(TextCompositionManager.PreviewTextInputUpdateEvent, new TextCompositionEventHandler(TerminalInputProxy_PreviewTextInputUpdate), handledEventsToo: true);
        TerminalInputProxy.AddHandler(TextCompositionManager.TextInputEvent, new TextCompositionEventHandler(TerminalInputProxy_TextInput), handledEventsToo: true);

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateInputProxyPosition();
        UpdateTerminalChrome();
        RequestDocumentRender();
        if (_hasStartedInitialSession)
        {
            return;
        }

        _hasStartedInitialSession = true;
        await StartTerminalAsync(focusTerminal: true);
    }

    public async Task CloseAsync()
    {
        if (_isClosingWindow)
        {
            return;
        }

        _isClosingWindow = true;
        _sessionWatchdog.Stop();
        _cursorBlinkTimer.Stop();
        _renderThrottleTimer.Stop();
        ReleaseTerminalMouseCapture(force: true);
        ResetInputProxyText();
        UpdateUiState(_session is not null);
        try
        {
            await StopTerminalAsync(reportStopped: false, forceTerminate: true).WaitAsync(CloseShutdownTimeout);
        }
        catch (TimeoutException)
        {
        }
    }

    private void OnActivated(object? sender, EventArgs e)
    {
        EmitFocusReport(focused: true);
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        EmitFocusReport(focused: false);
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        _autoRecoveryAttempts = 0;
        _useCompatibilityMode = false;
        await StartTerminalAsync(focusTerminal: true);
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _autoRecoveryAttempts = 0;
        await StopTerminalAsync(reportStopped: true);
    }

    private async void RecoverButton_Click(object sender, RoutedEventArgs e)
    {
        await RecoverSessionAsync(isAutomatic: false);
    }

    private void TerminalOutput_HyperlinkActivated(object? sender, TerminalHyperlinkActivatedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri)
            {
                UseShellExecute = true
            });
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
            TryCaptureTerminalMouse();
            QueueTerminalInputFocus();
            e.Handled = true;
        }
    }

    private void TerminalOutput_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        QueueTerminalInputFocus();

        bool handled = TrySendMouseButtonEvent(e, pressed: false);
        ReleaseTerminalMouseCaptureIfIdle();
        if (handled)
        {
            e.Handled = true;
        }
    }

    private void TerminalOutput_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (TrySendMouseMoveEvent(e))
        {
            if (HasTrackedMouseButtonPressed())
            {
                TryCaptureTerminalMouse();
            }

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

    private void TerminalOutput_LostMouseCapture(object sender, MouseEventArgs e)
    {
        _terminalMouseCaptureActive = false;
    }

    private void TerminalOutput_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        FocusTerminalInput();
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

        if (!FlushInputProxyText())
        {
            ResetInputProxyText();
        }
        UpdateTerminalFocusState(focused: false);
    }

    private void TerminalInputProxy_PreviewTextInputStart(object sender, TextCompositionEventArgs e)
    {
        UpdateOverlayState();
    }

    private void TerminalInputProxy_PreviewTextInputUpdate(object sender, TextCompositionEventArgs e)
    {
        UpdateOverlayState();
    }

    private void TerminalInputProxy_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_resettingInputProxyText)
        {
            return;
        }

        UpdateOverlayState();
    }

    private void TerminalInputProxy_TextInput(object sender, TextCompositionEventArgs e)
    {
        if (_resettingInputProxyText)
        {
            return;
        }

        _pendingProxyFlushAfterImeConfirm = false;
        if (!HasPendingProxyText())
        {
            UpdateOverlayState();
            return;
        }

        _ = Dispatcher.BeginInvoke(FlushCommittedProxyText, DispatcherPriority.Input);
    }

    private void FlushCommittedProxyText()
    {
        if (!FlushInputProxyText())
        {
            UpdateOverlayState();
        }
    }

    private void TerminalOutput_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        string text = string.IsNullOrEmpty(e.Text) ? e.SystemText : e.Text;
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (SendTerminalText(text, prefixAltIfNeeded: true))
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

        if (TryHandleClipboardShortcut(e))
        {
            e.Handled = true;
            return;
        }

        if (IsImeInputInProgress(e))
        {
            return;
        }

        if (TryHandleEnterKey(e))
        {
            e.Handled = true;
            return;
        }

        _ = FlushInputProxyText();

        if (TryHandleControlShortcut(e) || TryHandleApplicationKeypad(e) || TryHandleSpecialKey(e))
        {
            e.Handled = true;
        }
    }

    private void TerminalInputProxy_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_session is null)
        {
            return;
        }

        Key key = GetEffectiveKey(e);
        TerminalEnterAction enterAction = TerminalEnterActionResolver.ResolveForProxy(
            key,
            HasPendingProxyText());
        if (TryHandleClipboardShortcut(e))
        {
            e.Handled = true;
            return;
        }

        if (enterAction == TerminalEnterAction.FlushPendingProxyText)
        {
            QueuePendingProxyTextFlushAfterImeConfirm();
            return;
        }

        if (enterAction == TerminalEnterAction.SendToTerminal)
        {
            if (TryHandleEnterKey(e))
            {
                e.Handled = true;
            }

            return;
        }

        if (HasPendingProxyText())
        {
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
        if (HasPendingProxyText())
        {
            return true;
        }

        Key key = GetEffectiveKey(e);
        return key == Key.ImeProcessed ||
            key == Key.ImeConvert ||
            key == Key.ImeNonConvert ||
            key == Key.ImeAccept ||
            key == Key.ImeModeChange;
    }

    private bool TryHandleControlShortcut(KeyEventArgs e)
    {
        if (!SupportsTerminalInput())
        {
            return false;
        }

        ModifierKeys modifiers = GetTerminalModifiers();
        if ((modifiers & ModifierKeys.Control) == 0)
        {
            return false;
        }

        Key key = GetEffectiveKey(e);
        string? chord = TerminalKeyChordTranslator.TranslateCtrlChord(key, modifiers);
        if (chord is null)
        {
            return false;
        }

        if (key == Key.C && modifiers == ModifierKeys.Control)
        {
            SendInterrupt();
            return true;
        }

        return SendTerminalInput(chord);
    }

    private bool TryHandleSpecialKey(KeyEventArgs e)
    {
        Key key = GetEffectiveKey(e);
        if (key == Key.Enter)
        {
            return false;
        }

        ModifierKeys modifiers = GetTerminalModifiers();
        bool requiresTerminalInput = key is not Key.Back and not Key.Tab and not Key.Escape;
        if (requiresTerminalInput && !SupportsTerminalInput())
        {
            return false;
        }

        string? sequence = TerminalKeyChordTranslator.TranslateSpecialKey(
            key,
            modifiers,
            _terminalBuffer.ApplicationCursorKeysEnabled);

        return sequence is not null && SendTerminalInput(sequence);
    }

    private bool TryHandleEnterKey(KeyEventArgs e)
    {
        if (GetEffectiveKey(e) != Key.Enter)
        {
            return false;
        }

        string? sequence = TerminalKeyChordTranslator.TranslateEnterKey(
            GetTerminalModifiers(),
            _terminalBuffer.ApplicationCursorKeysEnabled,
            SupportsTerminalInput());

        return sequence is not null && SendTerminalInput(sequence);
    }

    private bool TryHandleApplicationKeypad(KeyEventArgs e)
    {
        if (!SupportsTerminalInput() || !_terminalBuffer.ApplicationKeypadEnabled || Keyboard.Modifiers != ModifierKeys.None)
        {
            return false;
        }

        Key key = GetEffectiveKey(e);
        string? sequence = key switch
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
        UpdateTerminalViewportSize();
    }

    private void UpdateTerminalViewportSize()
    {
        var (columns, rows) = CalculateTerminalSize();
        if (columns == _currentColumns && rows == _currentRows)
        {
            UpdateTerminalChrome();
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

        if (!_session.Capabilities.SupportsResize)
        {
            if (!_reportedUnsupportedResize)
            {
                _reportedUnsupportedResize = true;
                SetStatus("Compatibility mode keeps the local viewport size only.");
            }

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

        UpdateTerminalChrome();
    }

    private async Task StartTerminalAsync(bool focusTerminal = false)
    {
        if (!TryBuildLaunchRequest(out string commandLine, out string workingDirectory))
        {
            return;
        }

        await _sessionLifecycleGate.WaitAsync();
        try
        {
            _isSessionTransitionActive = true;
            UpdateUiState(_session is not null);

            ITerminalSession? previousSession = DetachCurrentSession();
            Exception? stopError = await DisposeSessionAsync(previousSession);

            ClearPendingOutput();
            ReleaseTerminalMouseCapture(force: true);
            ResetInputProxyText();
            (_currentColumns, _currentRows) = CalculateTerminalSize();
            ReplaceTerminalBuffer(new AnsiTerminalBuffer(_currentColumns, _currentRows));
            _cursorBlinkVisible = true;
            _reportedUnsupportedResize = false;
            _prioritizeInitialOutputRender = true;
            UpdateOverlayState();
            UpdateUiState(isRunning: false);
            UpdateWindowTitle();
            RenderTerminal();

            if (stopError is not null)
            {
                SetStatus($"Previous session cleanup failed: {FormatExceptionMessage(stopError)}");
            }

            if (_isClosingWindow)
            {
                return;
            }

            ITerminalSession session = await CreateSessionAsync(commandLine, _currentColumns, _currentRows, workingDirectory);
            session.OutputReceived += OnOutputReceived;
            session.Exited += OnProcessExited;
            _session = session;

            try
            {
                await Task.Run(session.Start);
            }
            catch (Exception startException)
            {
                _ = DetachCurrentSession();
                Exception? startCleanupError = await DisposeSessionAsync(session);
                if (startCleanupError is not null)
                {
                    SetStatus($"Failed to start terminal: {FormatExceptionMessage(startException)} Cleanup: {FormatExceptionMessage(startCleanupError)}");
                    return;
                }

                throw;
            }

            if (_isClosingWindow)
            {
                ITerminalSession? closingSession = DetachCurrentSession();
                await DisposeSessionAsync(closingSession);
                return;
            }

            _cursorBlinkVisible = true;
            UpdateUiState(isRunning: true);
            UpdateActiveLaunchState(commandLine, workingDirectory);
            if (focusTerminal)
            {
                FocusTerminalInput();
            }
            SetStatus(BuildSessionStartedMessage(commandLine));
        }
        catch (Exception ex)
        {
            ClearActiveLaunchState();
            UpdateUiState(isRunning: false);
            UpdateWindowTitle();
            SetStatus($"Failed to start terminal: {FormatExceptionMessage(ex)}");
        }
        finally
        {
            _isSessionTransitionActive = false;
            UpdateUiState(_session is not null);
            _sessionLifecycleGate.Release();
        }
    }

    private async Task StopTerminalAsync(
        bool reportStopped,
        string? statusOverride = null,
        ITerminalSession? expectedSession = null,
        bool forceTerminate = false)
    {
        await _sessionLifecycleGate.WaitAsync();
        try
        {
            if (expectedSession is not null && !ReferenceEquals(expectedSession, _session))
            {
                return;
            }

            _isSessionTransitionActive = true;
            UpdateUiState(_session is not null);

            ITerminalSession? session = DetachCurrentSession();
            ClearActiveLaunchState();
            ClearPendingOutput();
            ReleaseTerminalMouseCapture(force: true);
            ResetInputProxyText();
            _prioritizeInitialOutputRender = false;
            UpdateOverlayState();
            UpdateUiState(isRunning: false);
            UpdateWindowTitle();

            if (forceTerminate && session is not null)
            {
                _ = await Task.Run(() => session.TryForceUnlock());
            }

            Exception? stopError = await DisposeSessionAsync(session);
            if (stopError is not null)
            {
                statusOverride = $"Failed to stop terminal: {FormatExceptionMessage(stopError)}";
            }

            if (statusOverride is not null)
            {
                SetStatus(statusOverride);
            }
            else if (reportStopped)
            {
                SetStatus("Stopped.");
            }
        }
        finally
        {
            _isSessionTransitionActive = false;
            UpdateUiState(_session is not null);
            _sessionLifecycleGate.Release();
        }
    }

    private ITerminalSession? DetachCurrentSession()
    {
        ITerminalSession? session = _session;
        if (session is null)
        {
            return null;
        }

        session.OutputReceived -= OnOutputReceived;
        session.Exited -= OnProcessExited;
        _session = null;
        return session;
    }

    private async Task<ITerminalSession> CreateSessionAsync(string commandLine, short columns, short rows, string workingDirectory)
    {
        if (_useCompatibilityMode)
        {
            return await Task.Run(() => (ITerminalSession)new ProcessPipeSession(commandLine, columns, rows, workingDirectory));
        }

        try
        {
            return await Task.Run(() => (ITerminalSession)new ConPtySession(columns, rows, commandLine, workingDirectory));
        }
        catch
        {
            _useCompatibilityMode = true;
            return await Task.Run(() => (ITerminalSession)new ProcessPipeSession(commandLine, columns, rows, workingDirectory));
        }
    }

    private static async Task<Exception?> DisposeSessionAsync(ITerminalSession? session)
    {
        if (session is null)
        {
            return null;
        }

        try
        {
            await Task.Run(() => session.DisposeAsync().AsTask());
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private void OnOutputReceived(object? sender, string text)
    {
        if (sender is not ITerminalSession session || !ReferenceEquals(session, _session))
        {
            return;
        }

        QueueTerminalOutput(text);
    }

    private void OnProcessExited(object? sender, int exitCode)
    {
        if (sender is not ITerminalSession session)
        {
            return;
        }

        _ = Dispatcher.InvokeAsync(() =>
        {
            _ = HandleProcessExitedAsync(session, exitCode);
        }, DispatcherPriority.Normal);
    }

    private async Task HandleProcessExitedAsync(ITerminalSession session, int exitCode)
    {
        try
        {
            if (!ReferenceEquals(session, _session))
            {
                return;
            }

            FlushPendingOutput();
            await StopTerminalAsync(
                reportStopped: false,
                statusOverride: $"Process exited with code {exitCode}.",
                expectedSession: session);
        }
        catch (Exception ex)
        {
            SetStatus($"Exit handling failed: {FormatExceptionMessage(ex)}");
        }
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
        DispatcherPriority priority = DispatcherPriority.Normal;
        lock (_pendingOutputLock)
        {
            _pendingOutput.Append(text);
            if (!_outputFlushScheduled)
            {
                _outputFlushScheduled = true;
                shouldSchedule = true;
                if (_prioritizeInitialOutputRender)
                {
                    priority = DispatcherPriority.Normal;
                }
            }
        }

        if (shouldSchedule)
        {
            _ = Dispatcher.BeginInvoke(FlushPendingOutput, priority);
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
            bool prioritizeRender = _prioritizeInitialOutputRender;
            _prioritizeInitialOutputRender = false;
            RequestDocumentRender(immediate: prioritizeRender);
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
            _ = Dispatcher.BeginInvoke(FlushPendingOutput, DispatcherPriority.Normal);
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
        if (_session is not null && !_session.Capabilities.SupportsTerminalInput)
        {
            SetStatus("Interrupt signaling requires ConPTY mode.");
            return;
        }

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
        string text = TerminalOutput.GetSelectedText();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        Clipboard.SetText(text);
        SetStatus("Copied selection.");
    }

    private void UpdateUiState(bool isRunning)
    {
        bool isBusy = _isSessionTransitionActive || _isRecovering || _isClosingWindow;
        StartButton.IsEnabled = !isRunning && !isBusy;
        StopButton.IsEnabled = isRunning && !isBusy;
        RecoverButton.IsEnabled = isRunning && !isBusy;
        RestartButton.IsEnabled = isRunning && !isBusy;
        PasteButton.IsEnabled = isRunning && !isBusy;
        InterruptButton.IsEnabled = isRunning && !isBusy && SupportsTerminalInput();
        CommandTextBox.IsEnabled = !isRunning && !isBusy;
        ProfileComboBox.IsEnabled = !isRunning && !isBusy;
        WorkingDirectoryTextBox.IsEnabled = !isRunning && !isBusy;
        WorkingDirectoryHereButton.IsEnabled = !isRunning && !isBusy;
    }

    private void RequestDocumentRender(bool immediate = false)
    {
        if (immediate)
        {
            _renderThrottleTimer.Stop();
            if (Dispatcher.CheckAccess())
            {
                _documentRenderScheduled = false;
                PerformDocumentRender();
                return;
            }

            _documentRenderScheduled = true;
            _ = Dispatcher.BeginInvoke(PerformDocumentRender, DispatcherPriority.Normal);
            return;
        }

        if (_documentRenderScheduled || _renderThrottleTimer.IsEnabled)
        {
            return;
        }

        TimeSpan elapsed = DateTime.UtcNow - _lastDocumentRenderUtc;
        if (elapsed >= MinDocumentRenderInterval || _lastDocumentRenderUtc == DateTime.MinValue)
        {
            _documentRenderScheduled = true;
            _ = Dispatcher.BeginInvoke(PerformDocumentRender, DispatcherPriority.Render);
            return;
        }

        _renderThrottleTimer.Interval = MinDocumentRenderInterval - elapsed;
        _renderThrottleTimer.Start();
    }

    private void PerformDocumentRender()
    {
        _documentRenderScheduled = false;
        RenderTerminal();
    }

    private void RenderThrottleTimer_Tick(object? sender, EventArgs e)
    {
        _renderThrottleTimer.Stop();
        if (_documentRenderScheduled)
        {
            return;
        }

        _documentRenderScheduled = true;
        _ = Dispatcher.BeginInvoke(PerformDocumentRender, DispatcherPriority.Render);
    }

    private void RenderTerminal()
    {
        if (_isRenderingTerminal)
        {
            RequestDocumentRender();
            return;
        }

        double preservedDistanceFromBottom = GetDistanceFromBottom();
        _isRenderingTerminal = true;
        try
        {
            AnsiTerminalBuffer.TerminalRenderSnapshot snapshot = _terminalBuffer.CreateRenderSnapshot(showCursor: false);
            TerminalOutput.UpdateSnapshot(snapshot);
            UpdateInputProxyPosition();
            RestoreTerminalViewport(preservedDistanceFromBottom);
            UpdateWindowTitle();
            UpdateFindMatchCount();
            UpdateTerminalChrome();
        }
        finally
        {
            _lastDocumentRenderUtc = DateTime.UtcNow;
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

        FocusTerminalInput();
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
        TerminalViewportMetrics viewport = GetTerminalViewportMetrics();
        Size proxyTextSize = string.IsNullOrEmpty(TerminalInputProxy.Text)
            ? new Size(charWidth, charHeight)
            : MeasureTerminalText(TerminalInputProxy.Text);
        double proxyWidth = Math.Max(2, Math.Max(charWidth, Math.Ceiling(proxyTextSize.Width + 4)));
        double proxyHeight = Math.Max(2, Math.Max(charHeight, Math.Ceiling(proxyTextSize.Height)));
        TerminalInputProxy.Width = proxyWidth;
        TerminalInputProxy.Height = proxyHeight;
        TerminalInputProxy.FlowDirection = FlowDirection.LeftToRight;
        TerminalInputProxy.CaretBrush = Brushes.Transparent;

        int absoluteCursorLine = Math.Clamp(
            _terminalBuffer.ScrollbackLineCount + _terminalBuffer.CursorRow,
            0,
            Math.Max(0, TerminalOutput.LineCount - 1));
        Rect cursorRect = TerminalOutput.GetCellRect(absoluteCursorLine, _terminalBuffer.CursorColumn);
        Point translated = TerminalOutput.TranslatePoint(cursorRect.TopLeft, TerminalViewportHost);
        double left = translated.X;
        double top = translated.Y;
        if (double.IsNaN(left) || double.IsNaN(top))
        {
            left = viewport.ContentLeft + (_terminalBuffer.CursorColumn * charWidth);
            top = viewport.ContentTop + (absoluteCursorLine * charHeight);
        }

        (double proxyLeft, double proxyTop) = ClampToViewport(left, top, proxyWidth, proxyHeight, viewport);
        Canvas.SetLeft(TerminalInputProxy, proxyLeft);
        Canvas.SetTop(TerminalInputProxy, proxyTop);
        UpdateCursorOverlay(proxyLeft + GetInputProxyCaretOffset(), proxyTop, charWidth, charHeight, viewport);
    }

    private void ResetInputProxyText()
    {
        _pendingProxyFlushAfterImeConfirm = false;
        if (!string.IsNullOrEmpty(TerminalInputProxy.Text))
        {
            _resettingInputProxyText = true;
            try
            {
                TerminalInputProxy.Clear();
            }
            finally
            {
                _resettingInputProxyText = false;
            }
        }

        UpdateOverlayState();
    }

    private void UpdateCursorOverlay(double left, double top, double charWidth, double charHeight, TerminalViewportMetrics viewport)
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

        (double overlayLeft, double overlayTop) = ClampToViewport(left, top, overlayWidth, overlayHeight, viewport);

        TerminalCursorOverlay.Width = overlayWidth;
        TerminalCursorOverlay.Height = overlayHeight;
        TerminalCursorOverlay.Background = background;
        Canvas.SetLeft(TerminalCursorOverlay, overlayLeft);
        Canvas.SetTop(TerminalCursorOverlay, overlayTop);
        TerminalCursorOverlay.Visibility = Visibility.Visible;
    }

    private static (double Left, double Top) ClampToViewport(double left, double top, double width, double height, TerminalViewportMetrics viewport)
    {
        double maxLeft = Math.Max(viewport.ViewportLeft, viewport.ViewportRight - width);
        double maxTop = Math.Max(viewport.ViewportTop, viewport.ViewportBottom - height);
        return (
            Math.Clamp(left, viewport.ViewportLeft, maxLeft),
            Math.Clamp(top, viewport.ViewportTop, maxTop));
    }

    private bool ShouldShowCursorOverlay()
    {
        return ShouldShowCursor() &&
            _terminalBuffer.CursorVisible;
    }

    private static Key GetEffectiveKey(KeyEventArgs e)
    {
        Key key = e.Key == Key.ImeProcessed && e.ImeProcessedKey != Key.None
            ? e.ImeProcessedKey
            : e.Key;

        if (key == Key.ImeProcessed)
        {
            key = ResolveImeProcessedSpecialKey();
        }

        return key == Key.Return ? Key.Enter : key;
    }

    private static Key ResolveImeProcessedSpecialKey()
    {
        if (Keyboard.IsKeyDown(Key.Enter) || Keyboard.IsKeyDown(Key.Return))
        {
            return Key.Enter;
        }

        if (Keyboard.IsKeyDown(Key.Back))
        {
            return Key.Back;
        }

        if (Keyboard.IsKeyDown(Key.Tab))
        {
            return Key.Tab;
        }

        if (Keyboard.IsKeyDown(Key.Escape))
        {
            return Key.Escape;
        }

        if (Keyboard.IsKeyDown(Key.Up))
        {
            return Key.Up;
        }

        if (Keyboard.IsKeyDown(Key.Down))
        {
            return Key.Down;
        }

        if (Keyboard.IsKeyDown(Key.Left))
        {
            return Key.Left;
        }

        if (Keyboard.IsKeyDown(Key.Right))
        {
            return Key.Right;
        }

        if (Keyboard.IsKeyDown(Key.Home))
        {
            return Key.Home;
        }

        if (Keyboard.IsKeyDown(Key.End))
        {
            return Key.End;
        }

        if (Keyboard.IsKeyDown(Key.Insert))
        {
            return Key.Insert;
        }

        if (Keyboard.IsKeyDown(Key.Delete))
        {
            return Key.Delete;
        }

        if (Keyboard.IsKeyDown(Key.PageUp))
        {
            return Key.PageUp;
        }

        if (Keyboard.IsKeyDown(Key.PageDown))
        {
            return Key.PageDown;
        }

        return Key.ImeProcessed;
    }

    private double GetInputProxyCaretOffset()
    {
        string text = TerminalInputProxy.Text;
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        return Math.Ceiling(MeasureTerminalText(text).Width);
    }

    private bool HasPendingProxyText()
    {
        return !string.IsNullOrEmpty(TerminalInputProxy.Text);
    }

    private void UpdateOverlayState()
    {
        if (_isRenderingTerminal)
        {
            return;
        }

        UpdateInputProxyPosition();
    }

    private bool SendTerminalText(string text, bool prefixAltIfNeeded)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        if (prefixAltIfNeeded)
        {
            ModifierKeys modifiers = GetTerminalModifiers();
            if ((modifiers & ModifierKeys.Alt) != 0 && (modifiers & ModifierKeys.Control) == 0)
            {
                text = $"\u001b{text}";
            }
        }

        return SendTerminalInput(text);
    }

    private bool FlushInputProxyText()
    {
        if (_resettingInputProxyText || !HasPendingProxyText())
        {
            return false;
        }

        if (!SendTerminalText(TerminalInputProxy.Text, prefixAltIfNeeded: true))
        {
            return false;
        }

        ResetInputProxyText();
        return true;
    }

    private void QueuePendingProxyTextFlushAfterImeConfirm()
    {
        if (_pendingProxyFlushAfterImeConfirm)
        {
            return;
        }

        _pendingProxyFlushAfterImeConfirm = true;
        _ = Dispatcher.BeginInvoke(FlushPendingProxyTextAfterImeConfirm, DispatcherPriority.Background);
    }

    private void FlushPendingProxyTextAfterImeConfirm()
    {
        if (!_pendingProxyFlushAfterImeConfirm)
        {
            return;
        }

        _pendingProxyFlushAfterImeConfirm = false;
        if (!FlushInputProxyText())
        {
            UpdateOverlayState();
        }
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
        return Math.Max(
            0,
            TerminalScrollHost.ExtentHeight - TerminalScrollHost.VerticalOffset - TerminalScrollHost.ViewportHeight);
    }

    private void RestoreTerminalViewport(double preservedDistanceFromBottom)
    {
        if (_followTerminalOutput || preservedDistanceFromBottom <= AutoFollowThreshold)
        {
            TerminalScrollHost.ScrollToBottom();
        }
        else
        {
            double targetOffset = Math.Max(
                0,
                TerminalScrollHost.ExtentHeight - TerminalScrollHost.ViewportHeight - preservedDistanceFromBottom);
            TerminalScrollHost.ScrollToVerticalOffset(targetOffset);
        }

        UpdateFollowOutputState();
    }

    private void UpdateFollowOutputState()
    {
        _followTerminalOutput = GetDistanceFromBottom() <= AutoFollowThreshold;
        UpdateTerminalChrome();
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
        if (!_terminalBuffer.FocusReportingEnabled || !SupportsTerminalInput())
        {
            return;
        }

        _ = SendTerminalInput(focused ? "\u001b[I" : "\u001b[O");
    }

    private bool TrySendMouseButtonEvent(MouseButtonEventArgs e, bool pressed)
    {
        if (_session is null || !SupportsTerminalInput() || _terminalBuffer.MouseTrackingMode == TerminalMouseTrackingMode.Off)
        {
            return false;
        }

        if (!TryGetMouseCell(e.GetPosition(TerminalScrollHost), out int column, out int row))
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
        if (_session is null || !SupportsTerminalInput())
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

        if (!TryGetMouseCell(e.GetPosition(TerminalScrollHost), out int column, out int row))
        {
            return false;
        }

        int button = ResolveCurrentMouseButton(e);
        return SendMouseSequence(button, column, row, released: false, motion: true, wheel: false, wheelUp: false);
    }

    private bool TrySendMouseWheelEvent(MouseWheelEventArgs e)
    {
        if (_session is null || !SupportsTerminalInput())
        {
            return false;
        }

        if (_terminalBuffer.MouseTrackingMode == TerminalMouseTrackingMode.Off)
        {
            return TrySendAlternateScrollEvent(e);
        }

        if (!TryGetMouseCell(e.GetPosition(TerminalScrollHost), out int column, out int row))
        {
            return false;
        }

        bool wheelUp = e.Delta > 0;
        return SendMouseSequence(0, column, row, released: false, motion: false, wheel: true, wheelUp: wheelUp);
    }

    private bool TrySendAlternateScrollEvent(MouseWheelEventArgs e)
    {
        if (!_terminalBuffer.AlternateScrollEnabled || !_terminalBuffer.IsAlternateScreenActive)
        {
            return false;
        }

        Key directionKey = e.Delta > 0 ? Key.Up : Key.Down;
        string? sequence = TerminalKeyChordTranslator.TranslateSpecialKey(
            directionKey,
            ModifierKeys.None,
            _terminalBuffer.ApplicationCursorKeysEnabled);
        if (sequence is null)
        {
            return false;
        }

        int repeats = Math.Max(1, Math.Abs(e.Delta) / Mouse.MouseWheelDeltaForOneLine);
        var payload = new StringBuilder(sequence.Length * repeats);
        for (int index = 0; index < repeats; index++)
        {
            payload.Append(sequence);
        }

        return SendTerminalInput(payload.ToString());
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

    private void TryCaptureTerminalMouse()
    {
        if (_terminalMouseCaptureActive || !SupportsMouseCapture())
        {
            return;
        }

        if (Mouse.Capture(TerminalOutput, CaptureMode.Element))
        {
            _terminalMouseCaptureActive = true;
        }
    }

    private void ReleaseTerminalMouseCaptureIfIdle()
    {
        ReleaseTerminalMouseCapture(force: false);
    }

    private void ReleaseTerminalMouseCapture(bool force)
    {
        bool hasCapture = _terminalMouseCaptureActive || ReferenceEquals(Mouse.Captured, TerminalOutput);
        if (!hasCapture)
        {
            return;
        }

        if (!force && HasTrackedMouseButtonPressed())
        {
            return;
        }

        if (ReferenceEquals(Mouse.Captured, TerminalOutput))
        {
            Mouse.Capture(null);
        }

        _terminalMouseCaptureActive = false;
    }

    private bool SupportsMouseCapture()
    {
        return _session is not null &&
            SupportsTerminalInput() &&
            _terminalBuffer.MouseTrackingMode != TerminalMouseTrackingMode.Off;
    }

    private static bool HasTrackedMouseButtonPressed()
    {
        return Mouse.LeftButton == MouseButtonState.Pressed ||
            Mouse.MiddleButton == MouseButtonState.Pressed ||
            Mouse.RightButton == MouseButtonState.Pressed;
    }

    private static ModifierKeys GetTerminalModifiers()
    {
        return Keyboard.Modifiers & (ModifierKeys.Shift | ModifierKeys.Control | ModifierKeys.Alt);
    }

    private bool SupportsTerminalInput()
    {
        return _session?.Capabilities.SupportsTerminalInput ?? false;
    }

    private static string NormalizeClipboardSelectionTargets(string? selectionTargets)
    {
        return string.IsNullOrWhiteSpace(selectionTargets) ? "c" : selectionTargets.Trim();
    }

    private string BuildSessionStartedMessage(string commandLine)
    {
        if (_session is null)
        {
            return $"Started: {commandLine}";
        }

        return _session.Capabilities.Kind == TerminalSessionKind.ConPty
            ? $"Started ({_session.Capabilities.DisplayName}): {commandLine}"
            : $"Started ({_session.Capabilities.DisplayName}): {commandLine} [limited line mode]";
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
        Size size = TerminalOutput.CharacterCellSize;
        return (Math.Max(size.Width, 1.0), Math.Max(size.Height, 1.0));
    }

    private TerminalViewportMetrics GetTerminalViewportMetrics()
    {
        Point viewportOrigin = TerminalScrollHost.TranslatePoint(
            new Point(TerminalOutput.Padding.Left, TerminalOutput.Padding.Top),
            TerminalViewportHost);
        double horizontalOffset = TerminalScrollHost.HorizontalOffset;
        double verticalOffset = TerminalScrollHost.VerticalOffset;
        double viewportWidth = Math.Max(
            0,
            TerminalScrollHost.ViewportWidth - TerminalOutput.Padding.Left - TerminalOutput.Padding.Right);
        double viewportHeight = Math.Max(
            0,
            TerminalScrollHost.ViewportHeight - TerminalOutput.Padding.Top - TerminalOutput.Padding.Bottom);
        double viewportLeft = viewportOrigin.X;
        double viewportTop = viewportOrigin.Y;
        double viewportRight = viewportLeft + viewportWidth;
        double viewportBottom = viewportTop + viewportHeight;

        return new TerminalViewportMetrics(
            viewportLeft,
            viewportTop,
            viewportRight,
            viewportBottom,
            viewportLeft - horizontalOffset,
            viewportTop - verticalOffset,
            horizontalOffset,
            verticalOffset);
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
        string nextTitle = TerminalTabTitleResolver.Resolve(
            _terminalBuffer.WindowTitle,
            GetEffectiveTabTitleCommandLine(),
            GetSelectedProfile());
        if (string.Equals(HeaderTitle, nextTitle, StringComparison.Ordinal))
        {
            return;
        }

        HeaderTitle = nextTitle;
        HeaderTitleChanged?.Invoke(this, nextTitle);
    }

    private string GetEffectiveTabTitleCommandLine()
    {
        return string.IsNullOrWhiteSpace(_activeCommandLine)
            ? CommandTextBox.Text
            : _activeCommandLine;
    }

    private void SetStatus(string message)
    {
        StatusText.Text = $"[{DateTime.Now:HH:mm:ss}] {message}";
        UpdateTerminalChrome();
    }

    public string CommandLine => string.IsNullOrWhiteSpace(CommandTextBox.Text)
        ? TerminalProfileCatalog.BuildDefaultCommandLine()
        : CommandTextBox.Text.Trim();

    public string WorkingDirectory => string.IsNullOrWhiteSpace(WorkingDirectoryTextBox.Text)
        ? Environment.CurrentDirectory
        : WorkingDirectoryTextBox.Text.Trim();

    public void FocusTerminal()
    {
        FocusTerminalInput();
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
        if (_session is null || _isRecovering || _isSessionTransitionActive)
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
                ? "Initial output stalled in compatibility mode. Click Recover."
                : "Initial output stalled. Switching to compatibility mode via Recover.");
            return;
        }

        _autoRecoveryAttempts++;
        _ = RecoverSessionAsync(isAutomatic: true);
    }

    private async Task RecoverSessionAsync(bool isAutomatic)
    {
        if (_session is null || _isRecovering)
        {
            return;
        }

        _isRecovering = true;
        UpdateUiState(_session is not null);
        try
        {
            ITerminalSession? session = _session;
            if (session is null)
            {
                return;
            }

            _ = await Task.Run(() => session.TryForceUnlock());
            if (!_useCompatibilityMode)
            {
                _useCompatibilityMode = true;
            }

            SetStatus(isAutomatic
                ? "Initial output stalled. Unlocking and restarting in compatibility mode..."
                : "Recover requested. Unlocking and restarting in compatibility mode...");
            await StartTerminalAsync(focusTerminal: true);
        }
        catch (Exception ex)
        {
            SetStatus($"Recovery failed: {FormatExceptionMessage(ex)}");
        }
        finally
        {
            _isRecovering = false;
            UpdateUiState(_session is not null);
        }
    }

    private (short Columns, short Rows) CalculateTerminalSize()
    {
        TerminalViewportMetrics viewport = GetTerminalViewportMetrics();
        double viewportWidth = viewport.ViewportRight - viewport.ViewportLeft;
        double viewportHeight = viewport.ViewportBottom - viewport.ViewportTop;
        var (charWidth, charHeight) = MeasureCharacterCell();

        short columns = TerminalViewportSizing.CalculateCellCount(
            viewportWidth,
            charWidth,
            fallback: 120,
            min: 20,
            max: 500);
        short rows = TerminalViewportSizing.CalculateCellCount(
            viewportHeight,
            charHeight,
            fallback: 30,
            min: 10,
            max: 300);

        return (columns, rows);
    }

    private readonly record struct TerminalViewportMetrics(
        double ViewportLeft,
        double ViewportTop,
        double ViewportRight,
        double ViewportBottom,
        double ContentLeft,
        double ContentTop,
        double HorizontalOffset,
        double VerticalOffset);
}
