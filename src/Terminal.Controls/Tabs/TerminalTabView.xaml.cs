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

using Terminal.Buffer;
using Terminal.Input;
using Terminal.Logging;
using Terminal.Rendering;
using Terminal.Sessions;
using Terminal.Settings;

namespace Terminal.Tabs;

public partial class TerminalTabView : UserControl
{
    private const int MaxAutoRecoveryAttempts = 1;
    private const double AutoFollowThreshold = 2.0;

    private static readonly TimeSpan InitialOutputTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan IdleOutputTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CursorBlinkInterval = TimeSpan.FromMilliseconds(530);
    private static readonly TimeSpan MinDocumentRenderInterval = TimeSpan.FromMilliseconds(33);
    private static readonly TimeSpan SynchronizedUpdateRenderTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ExitOutputDrainInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan CloseShutdownTimeout = TimeSpan.FromSeconds(2);
    private const int ExitOutputDrainPasses = 3;
    private static readonly Brush BlockCursorBrush = CreateFrozenBrush(Color.FromArgb(0xA0, 0xE3, 0xE3, 0xE3));
    private static readonly Brush AccentCursorBrush = CreateFrozenBrush(Color.FromRgb(0x5F, 0xAF, 0xFF));

    private readonly TerminalSessionOrchestrator _sessionOrchestrator = new();
    private readonly TerminalKeyboardCoordinator _keyboardState = new();
    private readonly TerminalMouseCoordinator _mouseState = new();
    private readonly TerminalClipboardCoordinator _clipboardState = new();
    private ITerminalSession? _session => _sessionOrchestrator.Current;
    private AnsiTerminalBuffer _terminalBuffer = new(120, 30);
    private short _currentColumns = 120;
    private short _currentRows = 30;
    private readonly DispatcherTimer _sessionWatchdog = new(DispatcherPriority.Background);
    private readonly DispatcherTimer _cursorBlinkTimer = new(DispatcherPriority.Background);
    private readonly DispatcherTimer _renderThrottleTimer = new(DispatcherPriority.Background);
    private readonly DispatcherTimer _synchronizedUpdateWatchdogTimer = new(DispatcherPriority.Background);
    private readonly DispatcherTimer _toastDismissTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(4) };
    private System.Windows.Controls.Primitives.Popup? _toastPopup;
    private readonly TerminalOutputBatchCoordinator _outputBatch = new();
    private readonly TerminalRenderCoordinator _renderCoordinator = new();
    private bool _isRecovering => _sessionOrchestrator.IsRecovering;
    private bool _isSessionTransitionActive => _sessionOrchestrator.IsTransitionActive;
    private bool _isClosingWindow => _sessionOrchestrator.IsClosing;
    private bool _isRenderingTerminal => _renderCoordinator.IsRendering;
    private readonly TerminalViewportCoordinator _viewportState = new(AutoFollowThreshold);
    private bool _cursorBlinkVisible = true;
    private readonly TerminalImeInputCoordinator _imeInput = new();
    private bool _terminalMouseCaptureActive;
    private bool _localMouseSelectionActive;
    private bool _overlayUpdateQueued;
    private bool _terminalViewportSizeUpdateQueued;
    private readonly string _initialCommandLine;
    private readonly string _initialWorkingDirectory;
    private bool _hasStartedInitialSession;
    private readonly TerminalCommandNavigationCoordinator _commandNavigation = new();

    // Command lines reported via OSC 633;E, most-recent last. Backs the Ctrl+R
    // history overlay and the public CommandHistory API. Capped to keep memory bounded.
    private const int CommandHistoryLimit = 5000;
    private readonly TerminalHistoryCoordinator _historyState = new(CommandHistoryLimit);
    private string? _shellHistoryPath;

    // Number of items the right-click menu ships with from XAML (Copy / Paste). Host-provided
    // items are appended past this index and removed before each opening so they don't accumulate.
    private int _builtinContextMenuItemCount = -1;

    public event EventHandler<string>? HeaderTitleChanged;

    /// <summary>
    /// Raised on the dispatcher thread for every OSC 133 shell-integration marker observed
    /// on this session: prompt shown, command input started, command executing, and command
    /// finished (with exit code). Fires for commands typed by the user as well as those sent
    /// via <see cref="RunCommandAsync"/>, so hosts can surface live command activity
    /// (busy indicators, success/failure badges) without polling.
    /// </summary>
    public event EventHandler<ShellCommandActivityEventArgs>? ShellCommandActivity;

    /// <summary>
    /// Raised on the dispatcher thread whenever a command line is appended to
    /// <see cref="CommandHistory"/> (reported by the shell via OSC 633;E). The
    /// argument is the newly recorded command. Lets a host mirror the history in
    /// its own command palette without polling.
    /// </summary>
    public event EventHandler<string>? CommandHistoryRecorded;

    /// <summary>
    /// The shell command lines observed on this tab via OSC 633;E shell
    /// integration, oldest first, deduplicated (a repeat moves to the end).
    /// Backs the built-in Ctrl+R history overlay; exposed so hosts can drive
    /// their own history search UI. Empty until shell integration is active.
    /// </summary>
    public IReadOnlyList<string> CommandHistory => _historyState.History;

    /// <summary>
    /// When true (default), the first time the Ctrl+R history search opens for a
    /// PowerShell session, the tab seeds <see cref="CommandHistory"/> from
    /// PSReadLine's persistent on-disk history so commands from previous
    /// sessions are searchable. Set to false to keep history limited to commands
    /// observed in the current session.
    /// </summary>
    public bool PSReadLineHistorySeedingEnabled { get; set; } = true;

    /// <summary>
    /// Merges <paramref name="commands"/> (oldest first) into
    /// <see cref="CommandHistory"/>, deduplicating so the most recent occurrence
    /// wins. Lets a host pre-load history from its own store. Suppresses the
    /// automatic PSReadLine seeding for this tab.
    /// </summary>
    public void LoadCommandHistory(IEnumerable<string> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        _historyState.MarkSeeded();
        _historyState.MergeSeedHistory([.. commands]);
        if (HistoryPopup.IsOpen)
        {
            UpdateHistoryResults();
        }
    }

    /// <summary>
    /// Raised when a terminal hyperlink (OSC 8) is activated, before the default browser launch.
    /// A host may set <see cref="TerminalHyperlinkActivatedEventArgs.Handled"/> to <c>true</c> to
    /// take over opening the link (e.g. route it to an in-app browser); otherwise the URL is opened
    /// with the OS default browser via <c>Process.Start</c>.
    /// </summary>
    public event EventHandler<TerminalHyperlinkActivatedEventArgs>? HyperlinkActivated;

    /// <summary>
    /// Raised while the right-click context menu is opening, after the built-in Copy/Paste items.
    /// The menu only opens when there is a selection, so handlers can rely on
    /// <see cref="TerminalContextMenuBuildingEventArgs.SelectedText"/> being non-empty. Hosts append
    /// their own entries to <see cref="TerminalContextMenuBuildingEventArgs.Menu"/>; previously
    /// appended host items are cleared before each opening, so handlers add fresh items every time.
    /// </summary>
    public event EventHandler<TerminalContextMenuBuildingEventArgs>? ContextMenuBuilding;

    /// <summary>The currently selected terminal text (empty when nothing is selected).</summary>
    public string SelectedText => TerminalOutput.GetSelectedText();

    /// <summary>Whether the terminal currently has a non-empty selection.</summary>
    public bool HasSelection => TerminalOutput.HasSelection;

    public string HeaderTitle { get; private set; } = "Terminal";

    /// <summary>
    /// アプリケーションが ConEmu OSC 9;4 で報告したタスクバー進捗が変化したときに発火する
    /// （ディスパッチャスレッド）。ホストはウィンドウの <see cref="System.Windows.Shell.TaskbarItemInfo"/>
    /// にこの進捗を反映できる。
    /// </summary>
    public event EventHandler<TaskbarProgressChangedEventArgs>? TaskbarProgressChanged;

    /// <summary>直近に受信したタスクバー進捗の状態。タブ切替時にホストが読み出せる。</summary>
    public TaskbarProgressState CurrentTaskbarProgressState { get; private set; } = TaskbarProgressState.None;

    /// <summary>直近に受信したタスクバー進捗率（0–100）。</summary>
    public int CurrentTaskbarProgress { get; private set; }

    /// <summary>
    /// アプリケーションが BEL（0x07）を出力したときにディスパッチャスレッドで発火する。
    /// ホストは非アクティブタブのヘッダにベルインジケータを出すなどに利用できる。
    /// </summary>
    public event EventHandler? BellRang;

    /// <summary>
    /// 直近のベルがまだ確認（このタブの表示）されていないかどうか。ホストがタブヘッダの
    /// ベルインジケータ表示に用い、タブがアクティブになったら <see cref="ClearPendingBell"/> でクリアする。
    /// </summary>
    public bool HasPendingBell { get; private set; }

    /// <summary>未確認ベル状態をクリアする。ホストがタブをアクティブ表示した時点で呼ぶ。</summary>
    public void ClearPendingBell()
    {
        HasPendingBell = false;
    }

    public TerminalTabView(string? commandLine = null, string? workingDirectory = null)
    {
        _agentCommandOrchestrator = new(
            _agentCommands,
            new TerminalAgentCommandHost(
                () => _session is not null,
                () => _launchState.ActiveCommandLine,
                () => _terminalBuffer.ScrollbackLineCount + _terminalBuffer.CursorRow,
                SendTerminalInput,
                SendInterrupt,
                startLine => _terminalBuffer.GetPlainTextForAbsoluteLineRange(startLine, int.MaxValue),
                action => _ = Dispatcher.BeginInvoke(action)),
            new TerminalAgentTimeoutScheduler(),
            AgentCommandTimeout);
        _initialCommandLine = string.IsNullOrWhiteSpace(commandLine)
            ? TerminalProfileCatalog.BuildDefaultCommandLine()
            : commandLine.Trim();
        _initialWorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
            ? Environment.CurrentDirectory
            : workingDirectory.Trim();

        InitializeComponent();
        _builtinContextMenuItemCount = TerminalOutput.ContextMenu?.Items.Count ?? 0;
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
        _synchronizedUpdateWatchdogTimer.Interval = SynchronizedUpdateRenderTimeout;
        _synchronizedUpdateWatchdogTimer.Tick += SynchronizedUpdateWatchdogTimer_Tick;
        _toastDismissTimer.Tick += ToastDismissTimer_Tick;

        TerminalOutput.HyperlinkActivated += TerminalOutput_HyperlinkActivated;
        TerminalInputProxy.AddHandler(TextCompositionManager.PreviewTextInputStartEvent, new TextCompositionEventHandler(TerminalInputProxy_PreviewTextInputStart), handledEventsToo: true);
        TerminalInputProxy.AddHandler(TextCompositionManager.PreviewTextInputUpdateEvent, new TextCompositionEventHandler(TerminalInputProxy_PreviewTextInputUpdate), handledEventsToo: true);
        TerminalInputProxy.AddHandler(TextCompositionManager.TextInputEvent, new TextCompositionEventHandler(TerminalInputProxy_TextInput), handledEventsToo: true);

        _terminalBuffer.InputSequenceGenerated += TerminalBuffer_InputSequenceGenerated;
        _terminalBuffer.ClipboardSetRequested += TerminalBuffer_ClipboardSetRequested;
        _terminalBuffer.ClipboardQueryRequested += TerminalBuffer_ClipboardQueryRequested;
        _terminalBuffer.CurrentDirectoryChanged += TerminalBuffer_CurrentDirectoryChanged;
        _terminalBuffer.NotificationRequested += TerminalBuffer_NotificationRequested;
        _terminalBuffer.TaskbarProgressChanged += TerminalBuffer_TaskbarProgressChanged;
        _terminalBuffer.BellReceived += TerminalBuffer_BellReceived;
        _terminalBuffer.ShellCommandZoneReceived += TerminalBuffer_ShellCommandZoneReceived;
        _terminalBuffer.ShellCommandLineReceived += TerminalBuffer_ShellCommandLineReceived;
        _terminalBuffer.ShellHistoryPathReceived += TerminalBuffer_ShellHistoryPathReceived;

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
        await StartTerminalAsync(focusTerminal: AutoFocusOnStart);
    }

    /// <summary>
    /// Whether the automatic initial session start (on first load) moves keyboard focus into
    /// the terminal once the session is running. Defaults to true (standalone-app behavior).
    /// Hosts embedding many views (or restoring a saved layout where another pane should own
    /// focus) can set this to false so a late ConPTY startup does not steal focus; explicit
    /// starts via the Start button still focus the terminal.
    /// </summary>
    public bool AutoFocusOnStart { get; set; } = true;

    public async Task CloseAsync()
    {
        if (!_sessionOrchestrator.TryBeginClose())
        {
            return;
        }

        _sessionWatchdog.Stop();
        _cursorBlinkTimer.Stop();
        _renderThrottleTimer.Stop();
        StopSynchronizedUpdateWatchdog();
        _toastDismissTimer.Stop();
        _toastPopup = null;
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
        _sessionOrchestrator.ResetRecoveryAttempts();
        await StartTerminalAsync(focusTerminal: true);
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _sessionOrchestrator.ResetRecoveryAttempts();
        await StopTerminalAsync(reportStopped: true);
    }

    private async void RecoverButton_Click(object sender, RoutedEventArgs e)
    {
        await RecoverSessionAsync(isAutomatic: false);
    }

    private void TerminalOutput_HyperlinkActivated(object? sender, TerminalHyperlinkActivatedEventArgs e)
    {
        // Give the host a chance to route the link itself (e.g. an in-app browser).
        HyperlinkActivated?.Invoke(this, e);
        if (e.Handled)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(e.Target)
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
        if (IsRightClick(e))
        {
            QueueTerminalInputFocus();
            return;
        }

        if (ShouldStartLocalMouseSelection(e))
        {
            _localMouseSelectionActive = true;
            ReleaseTerminalMouseCapture(force: true);
            return;
        }

        if (TrySendMouseButtonEvent(e, pressed: true))
        {
            TryCaptureTerminalMouse();
            QueueTerminalInputFocus();
            e.Handled = true;
        }
    }

    private void TerminalOutput_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (IsRightClick(e))
        {
            _localMouseSelectionActive = false;
            if (!TerminalOutput.HasSelection)
            {
                PasteFromClipboard();
                QueueTerminalInputFocus();
                e.Handled = true;
            }

            return;
        }

        if (_localMouseSelectionActive && e.ChangedButton == MouseButton.Left)
        {
            _localMouseSelectionActive = false;
            ReleaseTerminalMouseCapture(force: true);
            return;
        }

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
        if (_localMouseSelectionActive)
        {
            return;
        }

        if (TrySendMouseMoveEvent(e))
        {
            if (HasTrackedMouseButtonPressed())
            {
                TryCaptureTerminalMouse();
            }

            e.Handled = true;
        }
    }

    private static bool IsRightClick(MouseButtonEventArgs e)
    {
        return e.ChangedButton == MouseButton.Right;
    }

    private void TerminalOutput_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        bool hasSelection = TerminalOutput.HasSelection;
        CopySelectionMenuItem.IsEnabled = hasSelection;
        PasteMenuItem.IsEnabled = CanPasteFromClipboard();
        if (!hasSelection)
        {
            e.Handled = true;
            return;
        }

        RebuildHostContextMenuItems();
    }

    // Drop any host-provided items appended on a previous opening, then let the host append fresh
    // entries (e.g. "Ask AI", "Search the web") that act on the current selection.
    private void RebuildHostContextMenuItems()
    {
        if (ContextMenuBuilding is not { } handler
            || TerminalOutput.ContextMenu is not { } menu
            || _builtinContextMenuItemCount < 0)
        {
            return;
        }

        for (int i = menu.Items.Count - 1; i >= _builtinContextMenuItemCount; i--)
            menu.Items.RemoveAt(i);

        handler(this, new TerminalContextMenuBuildingEventArgs(SelectedText, HasSelection, menu));
    }

    private void CopySelectionMenuItem_Click(object sender, RoutedEventArgs e)
    {
        CopySelectionToClipboard();
        QueueTerminalInputFocus();
    }

    private void PasteMenuItem_Click(object sender, RoutedEventArgs e)
    {
        PasteFromClipboard();
        QueueTerminalInputFocus();
    }

    private static bool ShouldStartLocalMouseSelection(MouseButtonEventArgs e)
    {
        return e.ChangedButton == MouseButton.Left &&
            (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
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
        _imeInput.BeginOrUpdateComposition();
        QueueOverlayStateUpdate();
    }

    private void TerminalInputProxy_PreviewTextInputUpdate(object sender, TextCompositionEventArgs e)
    {
        _imeInput.BeginOrUpdateComposition();
        QueueOverlayStateUpdate();
    }

    private void TerminalInputProxy_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_imeInput.OnProxyTextChanged(HasPendingProxyText()))
        {
            return;
        }

        QueueOverlayStateUpdate();
    }

    private void TerminalInputProxy_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (!_imeInput.ShouldProcessSelectionChange())
        {
            return;
        }

        QueueOverlayStateUpdate();
    }

    private void TerminalInputProxy_TextInput(object sender, TextCompositionEventArgs e)
    {
        ImeCommitAction action = _imeInput.Commit(HasPendingProxyText());
        if (action == ImeCommitAction.None)
        {
            return;
        }

        if (action == ImeCommitAction.UpdateOverlay)
        {
            QueueOverlayStateUpdate();
            return;
        }

        _ = Dispatcher.BeginInvoke(FlushCommittedProxyText, DispatcherPriority.Input);
    }

    private void FlushCommittedProxyText()
    {
        if (!FlushInputProxyText())
        {
            QueueOverlayStateUpdate();
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
        TerminalKeyboardAction action = _keyboardState.Resolve(BuildKeyboardRequest(e, TerminalKeyboardSource.Output));
        e.Handled = ExecuteKeyboardAction(action, e);
    }

    private void TerminalInputProxy_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        TerminalKeyboardAction action = _keyboardState.Resolve(BuildKeyboardRequest(e, TerminalKeyboardSource.Proxy));
        e.Handled = ExecuteKeyboardAction(action, e);
    }

    private TerminalKeyboardRequest BuildKeyboardRequest(KeyEventArgs e, TerminalKeyboardSource source)
    {
        Key key = GetEffectiveKey(e);
        ModifierKeys terminalModifiers = GetTerminalModifiers();
        bool supportsInput = SupportsTerminalInput();
        string? controlSequence = supportsInput && (terminalModifiers & ModifierKeys.Control) != 0
            ? TerminalKeyChordTranslator.TranslateCtrlChord(key, terminalModifiers, _terminalBuffer.ModifyOtherKeysLevel)
            : null;
        string? enterSequence = key == Key.Enter
            ? TerminalKeyChordTranslator.TranslateEnterKey(
                terminalModifiers,
                _terminalBuffer.ApplicationCursorKeysEnabled,
                supportsInput,
                _terminalBuffer.ModifyOtherKeysLevel,
                _terminalBuffer.KittyKeyboardFlags)
            : null;
        bool specialRequiresInput = key is not Key.Back and not Key.Tab and not Key.Escape;
        string? specialSequence = key != Key.Enter && (!specialRequiresInput || supportsInput)
            ? TerminalKeyChordTranslator.TranslateSpecialKey(
                key,
                terminalModifiers,
                _terminalBuffer.ApplicationCursorKeysEnabled,
                _terminalBuffer.ModifyOtherKeysLevel,
                _terminalBuffer.KittyKeyboardFlags)
            : null;
        return new(
            source,
            MapKeyboardKey(key),
            MapKeyboardKey(e.Key),
            MapKeyboardModifiers(Keyboard.Modifiers),
            MapKeyboardModifiers(terminalModifiers),
            _session is not null,
            HasPendingProxyText(),
            IsImeInputInProgress(e),
            supportsInput,
            _terminalBuffer.ApplicationKeypadEnabled,
            controlSequence,
            enterSequence,
            specialSequence);
    }

    private bool ExecuteKeyboardAction(TerminalKeyboardAction action, KeyEventArgs e)
    {
        bool hasDeferredFallback = action.Kind is TerminalKeyboardActionKind.ScrollPreviousCommand or
            TerminalKeyboardActionKind.ScrollNextCommand;
        if (action.FlushProxyFirst && !hasDeferredFallback)
        {
            _ = FlushInputProxyText();
        }

        return action.Kind switch
        {
            TerminalKeyboardActionKind.Copy => Execute(CopySelectionToClipboard),
            TerminalKeyboardActionKind.Paste => Execute(PasteFromClipboard),
            TerminalKeyboardActionKind.ScrollPreviousCommand => ExecuteScrollAction(action, upward: true),
            TerminalKeyboardActionKind.ScrollNextCommand => ExecuteScrollAction(action, upward: false),
            TerminalKeyboardActionKind.OpenHistory => Execute(OpenHistoryPanel),
            TerminalKeyboardActionKind.OpenFind => Execute(OpenFindPanel),
            TerminalKeyboardActionKind.LocalSelection => TerminalOutput.MoveKeyboardCursor(GetEffectiveKey(e), extend: true),
            TerminalKeyboardActionKind.QueueProxyFlush => Execute(QueuePendingProxyTextFlushAfterImeConfirm, handled: false),
            TerminalKeyboardActionKind.Interrupt => Execute(SendInterrupt),
            TerminalKeyboardActionKind.SendText => action.Text is not null && SendTerminalInput(action.Text),
            _ => false
        };
    }

    private bool ExecuteScrollAction(TerminalKeyboardAction action, bool upward)
    {
        if (TryScrollToAdjacentCommandLine(upward))
        {
            return true;
        }

        if (action.FlushProxyFirst)
        {
            _ = FlushInputProxyText();
        }

        return action.Text is not null && SendTerminalInput(action.Text);
    }

    private static bool Execute(Action action, bool handled = true)
    {
        action();
        return handled;
    }

    private static TerminalKeyboardModifiers MapKeyboardModifiers(ModifierKeys modifiers)
    {
        TerminalKeyboardModifiers result = TerminalKeyboardModifiers.None;
        if ((modifiers & ModifierKeys.Shift) != 0) result |= TerminalKeyboardModifiers.Shift;
        if ((modifiers & ModifierKeys.Control) != 0) result |= TerminalKeyboardModifiers.Control;
        if ((modifiers & ModifierKeys.Alt) != 0) result |= TerminalKeyboardModifiers.Alt;
        if ((modifiers & ModifierKeys.Windows) != 0) result |= TerminalKeyboardModifiers.Windows;
        return result;
    }

    private static TerminalKeyboardKey MapKeyboardKey(Key key) => key switch
    {
        Key.Enter => TerminalKeyboardKey.Enter, Key.C => TerminalKeyboardKey.C,
        Key.V => TerminalKeyboardKey.V, Key.R => TerminalKeyboardKey.R, Key.F => TerminalKeyboardKey.F,
        Key.Insert => TerminalKeyboardKey.Insert, Key.Up => TerminalKeyboardKey.Up,
        Key.Down => TerminalKeyboardKey.Down, Key.Left => TerminalKeyboardKey.Left,
        Key.Right => TerminalKeyboardKey.Right, Key.NumPad0 => TerminalKeyboardKey.NumPad0,
        Key.NumPad1 => TerminalKeyboardKey.NumPad1, Key.NumPad2 => TerminalKeyboardKey.NumPad2,
        Key.NumPad3 => TerminalKeyboardKey.NumPad3, Key.NumPad4 => TerminalKeyboardKey.NumPad4,
        Key.NumPad5 => TerminalKeyboardKey.NumPad5, Key.NumPad6 => TerminalKeyboardKey.NumPad6,
        Key.NumPad7 => TerminalKeyboardKey.NumPad7, Key.NumPad8 => TerminalKeyboardKey.NumPad8,
        Key.NumPad9 => TerminalKeyboardKey.NumPad9, Key.Multiply => TerminalKeyboardKey.Multiply,
        Key.Add => TerminalKeyboardKey.Add, Key.Separator => TerminalKeyboardKey.Separator,
        Key.Subtract => TerminalKeyboardKey.Subtract, Key.Decimal => TerminalKeyboardKey.Decimal,
        Key.Divide => TerminalKeyboardKey.Divide, _ => TerminalKeyboardKey.Other
    };

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

    private void OnTerminalOutputSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateTerminalSurfaceViewportFloor();
        QueueTerminalViewportSizeUpdate();
    }

    private void UpdateTerminalSurfaceViewportFloor()
    {
        TerminalOutput.SetViewportFloor(ResolveTerminalScrollViewportSize(new Thickness(0)));
    }

    private void QueueTerminalViewportSizeUpdate()
    {
        if (_terminalViewportSizeUpdateQueued)
        {
            return;
        }

        _terminalViewportSizeUpdateQueued = true;
        _ = Dispatcher.BeginInvoke(() =>
        {
            _terminalViewportSizeUpdateQueued = false;
            UpdateTerminalViewportSize();
        }, DispatcherPriority.Loaded);
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

        try
        {
            UpdateUiState(_session is not null);
            TerminalSessionStartResult result = await _sessionOrchestrator.StartAsync(
                () => CreateSessionAsync(commandLine, _currentColumns, _currentRows, workingDirectory),
                WireSessionEvents,
                UnwireSessionEvents,
                ResetViewForSessionStart);
            if (result.PreviousCleanupError is not null)
            {
                SetStatus($"Previous session cleanup failed: {FormatExceptionMessage(result.PreviousCleanupError)}");
            }

            if (!result.Started)
            {
                if (result.Error is null)
                {
                    return;
                }

                ClearActiveLaunchState();
                UpdateUiState(isRunning: false);
                UpdateWindowTitle();
                string hint = ConPtyStartupDiagnostics.BuildDiagnosticHint(result.Error, commandLine);
                string message = $"Failed to start terminal: {FormatExceptionMessage(result.Error)}";
                if (result.CleanupError is not null)
                {
                    message = $"{message} Cleanup: {FormatExceptionMessage(result.CleanupError)}";
                }
                else if (!string.IsNullOrEmpty(hint))
                {
                    message = $"{message} — {hint}";
                }

                SetStatus(message);
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
            HandleStartFailureBestEffort(ex, commandLine);
        }
        finally
        {
            TryUiAction(() => UpdateUiState(_session is not null));
        }
    }

    private void HandleStartFailureBestEffort(Exception error, string commandLine)
    {
        TryUiAction(ClearActiveLaunchState);
        TryUiAction(() => UpdateUiState(isRunning: false));
        TryUiAction(UpdateWindowTitle);
        TryUiAction(() =>
        {
            string hint = ConPtyStartupDiagnostics.BuildDiagnosticHint(error, commandLine);
            string message = $"Failed to start terminal: {FormatExceptionMessage(error)}";
            SetStatus(string.IsNullOrEmpty(hint) ? message : $"{message} — {hint}");
        });
    }

    private static void TryUiAction(Action action)
    {
        try
        {
            action();
        }
        catch
        {
            // UI cleanup/status reporting is best effort during an existing failure.
        }
    }

    private async Task StopTerminalAsync(
        bool reportStopped,
        string? statusOverride = null,
        ITerminalSession? expectedSession = null,
        bool forceTerminate = false)
    {
        UpdateUiState(_session is not null);
        TerminalSessionStopResult result = await _sessionOrchestrator.StopAsync(
            expectedSession,
            forceTerminate,
            UnwireSessionEvents,
            ResetViewForSessionStop);
        try
        {
            if (!result.Applied)
            {
                return;
            }

            if (result.Error is not null)
            {
                statusOverride = $"Failed to stop terminal: {FormatExceptionMessage(result.Error)}";
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
            UpdateUiState(_session is not null);
        }
    }

    private void WireSessionEvents(ITerminalSession session)
    {
        session.OutputReceived += OnOutputReceived;
        session.Exited += OnProcessExited;
    }

    private void UnwireSessionEvents(ITerminalSession session)
    {
        session.OutputReceived -= OnOutputReceived;
        session.Exited -= OnProcessExited;
        AbortActiveAgentCommand();
    }

    private void ResetViewForSessionStart()
    {
        ClearPendingOutput();
        StopSynchronizedUpdateWatchdog();
        ReleaseTerminalMouseCapture(force: true);
        ResetInputProxyText();
        (_currentColumns, _currentRows) = CalculateTerminalSize();
        ReplaceTerminalBuffer(new AnsiTerminalBuffer(_currentColumns, _currentRows, _scrollbackLimit));
        _cursorBlinkVisible = true;
        _outputBatch.SetPrioritizeNextRender(true);
        UpdateOverlayState();
        UpdateUiState(isRunning: false);
        UpdateWindowTitle();
        RenderTerminal();
    }

    private void ResetViewForSessionStop()
    {
        ClearActiveLaunchState();
        ClearPendingOutput();
        StopSynchronizedUpdateWatchdog();
        ForceEndTransientModesAndRender();
        ReleaseTerminalMouseCapture(force: true);
        ResetInputProxyText();
        _outputBatch.SetPrioritizeNextRender(false);
        UpdateOverlayState();
        UpdateUiState(isRunning: false);
        UpdateWindowTitle();
    }

    private async Task<ITerminalSession> CreateSessionAsync(string commandLine, short columns, short rows, string workingDirectory)
    {
        bool injectShellIntegration = ShellIntegrationInjectionEnabled;
        return await Task.Run(() =>
        {
            TerminalAppSettings settings = TerminalAppSettings.Load();
            string launchCommandLine = injectShellIntegration
                ? ShellIntegration.PrepareLaunch(commandLine)
                : commandLine;
            ITerminalSession inner = new ConPtySession(
                columns,
                rows,
                launchCommandLine,
                workingDirectory,
                CreateTerminalEnvironmentVariables());
            if (!settings.EnableSessionLogging)
            {
                return inner;
            }

            try
            {
                ISessionLogger logger = SessionLogWriter.Create(commandLine, workingDirectory, settings.SessionLogDirectory);
                return (ITerminalSession)new LoggingTerminalSession(inner, logger, commandLine, workingDirectory, columns, rows);
            }
            catch
            {
                return inner;
            }
        });
    }

    private static IReadOnlyDictionary<string, string?> CreateTerminalEnvironmentVariables()
    {
        var variables = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        int gitConfigIndex = 0;
        string? gitConfigCount = Environment.GetEnvironmentVariable("GIT_CONFIG_COUNT");
        if (!string.IsNullOrWhiteSpace(gitConfigCount) &&
            int.TryParse(gitConfigCount, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedCount) &&
            parsedCount >= 0)
        {
            gitConfigIndex = parsedCount;
        }

        variables["GIT_CONFIG_COUNT"] = (gitConfigIndex + 1).ToString(CultureInfo.InvariantCulture);
        variables[$"GIT_CONFIG_KEY_{gitConfigIndex}"] = "core.quotepath";
        variables[$"GIT_CONFIG_VALUE_{gitConfigIndex}"] = "false";
        return variables;
    }

    private void OnOutputReceived(object? sender, string text)
    {
        if (sender is not ITerminalSession session || !_sessionOrchestrator.IsCurrent(session))
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
            TerminalSessionStopResult result = await _sessionOrchestrator.HandleExitAsync(
                session,
                ExitOutputDrainPasses,
                ExitOutputDrainInterval,
                force => FlushPendingOutput(forceEndTransientModes: force),
                UnwireSessionEvents,
                ResetViewForSessionStop);
            if (result.Error is not null)
            {
                SetStatus(result.ErrorKind == TerminalSessionStopErrorKind.Dispose
                    ? $"Failed to stop terminal: {FormatExceptionMessage(result.Error)}"
                    : $"Exit handling failed: {FormatExceptionMessage(result.Error)}");
            }
            else if (result.Applied)
            {
                SetStatus($"Process exited with code {exitCode}.");
            }
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

    public bool SendTerminalInput(string text)
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

        if (_outputBatch.Enqueue(text))
        {
            _ = Dispatcher.BeginInvoke(FlushPendingOutput, DispatcherPriority.Normal);
        }
    }

    private void FlushPendingOutput()
    {
        FlushPendingOutput(forceEndTransientModes: false);
    }

    private void FlushPendingOutput(bool forceEndTransientModes)
    {
        string? nextBatch = _outputBatch.Drain();

        if (!string.IsNullOrEmpty(nextBatch))
        {
            bool endedSynchronizedUpdate = _terminalBuffer.Process(nextBatch);
            TryCompleteAgentSentinel();
            bool prioritizeRender = _outputBatch.ConsumeRenderPriority();
            if (!_terminalBuffer.SynchronizedUpdateActive)
            {
                StopSynchronizedUpdateWatchdog();
                RequestDocumentRender(immediate: prioritizeRender || endedSynchronizedUpdate);
            }
            else
            {
                ScheduleSynchronizedUpdateWatchdog();
            }
        }

        if (forceEndTransientModes)
        {
            ForceEndTransientModesAndRender();
        }

        if (_outputBatch.EnsureFlushScheduled())
        {
            _ = Dispatcher.BeginInvoke(FlushPendingOutput, DispatcherPriority.Normal);
        }
    }

    private void ScheduleSynchronizedUpdateWatchdog()
    {
        if (!_renderCoordinator.ArmWatchdog())
        {
            return;
        }

        _synchronizedUpdateWatchdogTimer.Start();
    }

    private void StopSynchronizedUpdateWatchdog()
    {
        _renderCoordinator.DisarmWatchdog();
        _synchronizedUpdateWatchdogTimer.Stop();
    }

    private void SynchronizedUpdateWatchdogTimer_Tick(object? sender, EventArgs e)
    {
        _synchronizedUpdateWatchdogTimer.Stop();
        if (_renderCoordinator.ConsumeWatchdogTick())
        {
            ForceEndSynchronizedUpdateAndRender();
        }
    }

    private void ForceEndSynchronizedUpdateAndRender()
    {
        if (!_terminalBuffer.ForceEndSynchronizedUpdate())
        {
            StopSynchronizedUpdateWatchdog();
            return;
        }

        StopSynchronizedUpdateWatchdog();
        RequestDocumentRender(immediate: true);
    }

    private void ForceEndTransientModesAndRender()
    {
        bool changed = _terminalBuffer.ForceEndSynchronizedUpdate();
        changed |= _terminalBuffer.ForceExitAlternateScreen();
        if (!changed)
        {
            StopSynchronizedUpdateWatchdog();
            return;
        }

        StopSynchronizedUpdateWatchdog();
        RequestDocumentRender(immediate: true);
    }

    private void ClearPendingOutput()
    {
        _outputBatch.Clear();
    }

    public bool SendTerminalInput(byte[] bytes)
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
        bool hasSession = _session is not null;
        if (!hasSession)
        {
            return;
        }

        bool containsText = Clipboard.ContainsText();
        if (!_clipboardState.CanPaste(hasSession, containsText))
        {
            return;
        }

        string text = Clipboard.GetText();
        TerminalPasteAction action = _clipboardState.ResolvePaste(
            hasSession, containsText, text, _terminalBuffer.BracketedPasteEnabled, multilinePasteApproved: false);
        if (action.Kind == TerminalPasteActionKind.ConfirmMultiline)
        {
            var result = MessageBox.Show(
                Window.GetWindow(this),
                "クリップボードのテキストに改行が含まれています。\n複数行を一度に送信すると意図しないコマンド実行が起きる可能性があります。\n\n貼り付けますか？",
                "複数行貼り付けの確認",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);
            if (result != MessageBoxResult.OK)
            {
                return;
            }

            action = _clipboardState.ResolvePaste(
                hasSession, containsText, text, _terminalBuffer.BracketedPasteEnabled, multilinePasteApproved: true);
        }

        if (action is { Kind: TerminalPasteActionKind.Send, Text: not null })
        {
            _ = SendTerminalInput(action.Text);
        }
    }

    private bool CanPasteFromClipboard()
    {
        return _session is not null && _clipboardState.CanPaste(hasSession: true, Clipboard.ContainsText());
    }

    private void CopySelectionToClipboard()
    {
        string text = TerminalOutput.GetSelectedText();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        // 画面選択のコピーは色付き（HTML/RTF）とプレーンテキストを併載する。選択セルの解決済み
        // 前景/背景・装飾から CF_HTML と RTF を生成し、プレーンテキストと合わせて DataObject に
        // 載せる。貼り付け先が色付き形式に対応していなければ従来どおりプレーンテキストが使われる。
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, text);

        StyledSelection? styled = TerminalOutput.GetStyledSelection();
        if (styled is not null)
        {
            data.SetData(DataFormats.Html, ColoredClipboardWriter.BuildHtml(styled));
            data.SetData(DataFormats.Rtf, ColoredClipboardWriter.BuildRtf(styled));
        }

        Clipboard.SetDataObject(data, copy: true);
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
        TerminalRenderDecision decision = _renderCoordinator.RequestRender(
            immediate,
            Dispatcher.CheckAccess(),
            _renderThrottleTimer.IsEnabled,
            DateTime.UtcNow,
            MinDocumentRenderInterval);
        if (decision.StopThrottle)
        {
            _renderThrottleTimer.Stop();
        }

        switch (decision.Action)
        {
            case TerminalRenderAction.RenderNow:
                PerformDocumentRender();
                break;
            case TerminalRenderAction.Dispatch:
                _ = Dispatcher.BeginInvoke(
                    PerformDocumentRender,
                    immediate ? DispatcherPriority.Normal : DispatcherPriority.Render);
                break;
            case TerminalRenderAction.StartThrottle:
                _renderThrottleTimer.Interval = decision.ThrottleDelay;
                _renderThrottleTimer.Start();
                break;
        }
    }

    private void PerformDocumentRender()
    {
        _renderCoordinator.BeginDispatchedRender();
        RenderTerminal();
    }

    private void RenderThrottleTimer_Tick(object? sender, EventArgs e)
    {
        _renderThrottleTimer.Stop();
        if (_renderCoordinator.OnThrottleTick())
        {
            _ = Dispatcher.BeginInvoke(PerformDocumentRender, DispatcherPriority.Render);
        }
    }

    private void RenderTerminal()
    {
        if (_renderCoordinator.IsRendering)
        {
            RequestDocumentRender();
            return;
        }

        bool isAlternateScreenActive = _terminalBuffer.IsAlternateScreenActive;
        UpdateTerminalSurfaceViewportFloor();
        ApplyAlternateScreenViewportMode(isAlternateScreenActive);
        double preservedDistanceFromBottom = isAlternateScreenActive ? 0 : GetDistanceFromBottom();
        if (!_renderCoordinator.TryBeginRender())
        {
            RequestDocumentRender();
            return;
        }

        try
        {
            AnsiTerminalBuffer.TerminalRenderSnapshot snapshot = _terminalBuffer.CreateRenderSnapshot(showCursor: false);
            TerminalOutput.UpdateSnapshot(snapshot);
            // Restore the viewport (auto-follow scroll) BEFORE positioning the input proxy so the
            // IME composition window is anchored to the post-scroll content position. The
            // ScrollViewer's VerticalOffset is still stale until its deferred scroll lands, so the
            // resolved target offset is threaded into the proxy layout explicitly.
            double restoredVerticalOffset = RestoreTerminalViewport(preservedDistanceFromBottom);
            UpdateInputProxyPosition(restoredVerticalOffset);
            UpdateWindowTitle();
            UpdateFindMatchCount();
            UpdateTerminalChrome();
        }
        finally
        {
            _renderCoordinator.EndRender(DateTime.UtcNow);
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

    private void UpdateInputProxyPosition(double? overrideVerticalOffset = null)
    {
        var (charWidth, charHeight) = MeasureCharacterCell();
        TerminalViewportMetrics viewport = GetTerminalViewportMetrics(overrideVerticalOffset);
        Rect viewportBounds = new(
            viewport.ViewportLeft,
            viewport.ViewportTop,
            Math.Max(0, viewport.ViewportRight - viewport.ViewportLeft),
            Math.Max(0, viewport.ViewportBottom - viewport.ViewportTop));
        Size proxyTextSize = string.IsNullOrEmpty(TerminalInputProxy.Text)
            ? new Size(charWidth, charHeight)
            : MeasureTerminalText(TerminalInputProxy.Text);
        double proxyWidth = Math.Max(2, Math.Max(charWidth, Math.Ceiling(proxyTextSize.Width + 4)));
        double proxyHeight = Math.Max(2, Math.Max(charHeight, Math.Ceiling(proxyTextSize.Height)));
        TerminalInputProxy.Width = proxyWidth;
        TerminalInputProxy.Height = proxyHeight;
        TerminalInputProxy.FlowDirection = FlowDirection.LeftToRight;
        TerminalInputProxy.CaretBrush = Brushes.Transparent;

        int absoluteCursorLine = ResolveRenderedCursorLine(
            _terminalBuffer.CursorRow,
            _terminalBuffer.ScrollbackLineCount,
            _terminalBuffer.IsAlternateScreenActive,
            TerminalOutput.LineCount);
        double left = viewport.ContentLeft + (_terminalBuffer.CursorColumn * charWidth);
        double top = viewport.ContentTop + (absoluteCursorLine * charHeight);

        Rect proxyBounds = CalculateProxyBounds(left, top, proxyWidth, proxyHeight, viewportBounds);
        Canvas.SetLeft(TerminalInputProxy, proxyBounds.Left);
        Canvas.SetTop(TerminalInputProxy, proxyBounds.Top);
        TerminalInputProxy.UpdateLayout();

        Rect? proxyCaretBounds = ShouldUseProxyCaret()
            && TryGetInputProxyCaretBounds(
            proxyBounds,
            charHeight,
            out Rect resolvedProxyCaretBounds)
            ? resolvedProxyCaretBounds
            : null;
        (_, Rect cursorBounds) = CalculateOverlayLayout(
            left,
            top,
            proxyWidth,
            proxyHeight,
            charWidth,
            charHeight,
            viewportBounds,
            _terminalBuffer.CursorShape,
            proxyCaretBounds);
        UpdateCursorOverlay(cursorBounds);
    }

    private void ResetInputProxyText()
    {
        _imeInput.BeginReset();
        try
        {
            if (!string.IsNullOrEmpty(TerminalInputProxy.Text))
            {
                TerminalInputProxy.Clear();
            }
        }
        finally
        {
            _imeInput.EndReset();
        }

        QueueOverlayStateUpdate();
    }

    internal static (Rect ProxyBounds, Rect CursorBounds) CalculateOverlayLayout(
        double cursorLeft,
        double cursorTop,
        double proxyWidth,
        double proxyHeight,
        double charWidth,
        double charHeight,
        Rect viewportBounds,
        TerminalCursorShape cursorShape,
        Rect? proxyCaretBounds = null)
    {
        Rect proxyBounds = CalculateProxyBounds(cursorLeft, cursorTop, proxyWidth, proxyHeight, viewportBounds);
        double visualCursorLeft = proxyCaretBounds?.Left ?? cursorLeft;
        double visualCursorTop = proxyCaretBounds?.Top ?? cursorTop;
        Rect cursorBounds = CalculateCursorOverlayBounds(
            visualCursorLeft,
            visualCursorTop,
            charWidth,
            charHeight,
            viewportBounds,
            cursorShape);
        return (proxyBounds, cursorBounds);
    }

    internal static int ResolveRenderedCursorLine(
        int cursorRow,
        int scrollbackLineCount,
        bool isAlternateScreenActive,
        int renderedLineCount)
    {
        int renderedScrollbackCount = isAlternateScreenActive ? 0 : scrollbackLineCount;
        return Math.Clamp(
            renderedScrollbackCount + cursorRow,
            0,
            Math.Max(0, renderedLineCount - 1));
    }

    private void UpdateCursorOverlay(Rect bounds)
    {
        if (bounds.IsEmpty || !ShouldShowCursorOverlay())
        {
            TerminalCursorOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        Brush background = BlockCursorBrush;

        switch (_terminalBuffer.CursorShape)
        {
            case TerminalCursorShape.Underline:
                background = AccentCursorBrush;
                break;
            case TerminalCursorShape.Bar:
                background = AccentCursorBrush;
                break;
        }

        TerminalCursorOverlay.Width = bounds.Width;
        TerminalCursorOverlay.Height = bounds.Height;
        TerminalCursorOverlay.Background = background;
        Canvas.SetLeft(TerminalCursorOverlay, bounds.Left);
        Canvas.SetTop(TerminalCursorOverlay, bounds.Top);
        TerminalCursorOverlay.Visibility = Visibility.Visible;
    }

    private bool TryGetInputProxyCaretBounds(Rect proxyBounds, double charHeight, out Rect caretBounds)
    {
        caretBounds = Rect.Empty;
        if (!HasPendingProxyText())
        {
            return false;
        }

        int caretIndex = Math.Clamp(TerminalInputProxy.CaretIndex, 0, TerminalInputProxy.Text.Length);
        string prefix = caretIndex == 0
            ? string.Empty
            : TerminalInputProxy.Text[..caretIndex];
        double caretOffset = string.IsNullOrEmpty(prefix)
            ? 0
            : Math.Ceiling(MeasureTerminalText(prefix).Width);
        if (double.IsNaN(caretOffset) || double.IsInfinity(caretOffset))
        {
            return false;
        }

        caretBounds = new Rect(
            proxyBounds.Left + caretOffset,
            proxyBounds.Top,
            0,
            Math.Max(0, charHeight));
        return true;
    }

    private static Rect CalculateCursorOverlayBounds(
        double left,
        double top,
        double charWidth,
        double charHeight,
        Rect viewportBounds,
        TerminalCursorShape cursorShape)
    {
        // When the cursor's line has scrolled out of the visible viewport, hide the overlay
        // instead of pinning it to the viewport edge so it tracks the actual input position.
        if (top + charHeight <= viewportBounds.Top || top >= viewportBounds.Bottom)
        {
            return Rect.Empty;
        }

        double overlayWidth = Math.Max(2, Math.Ceiling(charWidth));
        double overlayHeight = Math.Max(2, Math.Ceiling(charHeight));

        switch (cursorShape)
        {
            case TerminalCursorShape.Underline:
                overlayHeight = Math.Max(2, Math.Ceiling(charHeight / 6));
                top += Math.Max(0, charHeight - overlayHeight);
                break;
            case TerminalCursorShape.Bar:
                overlayWidth = Math.Max(2, Math.Ceiling(charWidth / 6));
                break;
        }

        (double overlayLeft, double overlayTop) = ClampToViewport(left, top, overlayWidth, overlayHeight, viewportBounds);
        return new Rect(overlayLeft, overlayTop, overlayWidth, overlayHeight);
    }

    private static Rect CalculateProxyBounds(double left, double top, double width, double height, Rect viewportBounds)
    {
        (double proxyLeft, double proxyTop) = ClampToViewport(left, top, width, height, viewportBounds);
        return new Rect(proxyLeft, proxyTop, width, height);
    }

    private static (double Left, double Top) ClampToViewport(double left, double top, double width, double height, Rect viewportBounds)
    {
        double maxLeft = Math.Max(viewportBounds.Left, viewportBounds.Right - width);
        double maxTop = Math.Max(viewportBounds.Top, viewportBounds.Bottom - height);
        return (
            Math.Clamp(left, viewportBounds.Left, maxLeft),
            Math.Clamp(top, viewportBounds.Top, maxTop));
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

    private bool HasPendingProxyText()
    {
        return !string.IsNullOrEmpty(TerminalInputProxy.Text);
    }

    internal static bool ShouldUseProxyCaret(bool hasPendingProxyText, bool imeCompositionActive)
    {
        return TerminalImeInputCoordinator.ShouldUseProxyCaretForState(
            hasPendingProxyText,
            imeCompositionActive);
    }

    private bool ShouldUseProxyCaret()
    {
        return _imeInput.ShouldUseProxyCaret;
    }

    private void QueueOverlayStateUpdate()
    {
        if (_overlayUpdateQueued)
        {
            return;
        }

        _overlayUpdateQueued = true;
        _ = Dispatcher.BeginInvoke(() =>
        {
            _overlayUpdateQueued = false;
            UpdateOverlayState();
        }, DispatcherPriority.Render);
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
            if ((modifiers & ModifierKeys.Alt) != 0 && (modifiers & ModifierKeys.Control) == 0 &&
                _terminalBuffer.AltSendsEscape)
            {
                text = $"\u001b{text}";
            }
        }

        return SendTerminalInput(text);
    }

    private bool FlushInputProxyText()
    {
        _imeInput.OnProxyTextChanged(HasPendingProxyText());
        if (!_imeInput.CanFlushProxyText())
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
        if (!_imeInput.TryQueueDeferredFlush())
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(FlushPendingProxyTextAfterImeConfirm, DispatcherPriority.Background);
    }

    private void FlushPendingProxyTextAfterImeConfirm()
    {
        if (!_imeInput.TryConsumeDeferredFlush())
        {
            return;
        }

        if (!FlushInputProxyText())
        {
            QueueOverlayStateUpdate();
        }
    }

    private void TerminalScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (ShouldRefreshViewportSize(e.ViewportWidthChange, e.ViewportHeightChange))
        {
            QueueTerminalViewportSizeUpdate();
        }

        if (_isRenderingTerminal)
        {
            return;
        }

        UpdateFollowOutputState();
        // Reposition the cursor/input overlay so it tracks the content as the user scrolls
        // (and hides once the cursor line leaves the viewport) rather than staying pinned.
        QueueOverlayStateUpdate();
    }

    private double GetDistanceFromBottom()
    {
        return Math.Max(
            0,
            TerminalScrollHost.ExtentHeight - TerminalScrollHost.VerticalOffset - TerminalScrollHost.ViewportHeight);
    }

    private double RestoreTerminalViewport(double preservedDistanceFromBottom)
    {
        bool isAlternateScreenActive = _terminalBuffer.IsAlternateScreenActive;
        // Read the surface's freshly-updated extent/viewport rather than the ScrollViewer's.
        // UpdateSnapshot recomputes the surface metrics synchronously, but the ScrollViewer's
        // ExtentHeight/ViewportHeight are only refreshed on its next layout pass (after the
        // InvalidateScrollInfo queued here). Using the stale ScrollViewer extent would scroll
        // to the previous bottom and fail to follow newly appended output.
        // Derive the follow state from this decision instead of re-reading the ScrollViewer,
        // whose offset/extent are stale until the deferred scroll lands.
        double targetOffset = _viewportState.ResolveRestoredVerticalOffset(
            isAlternateScreenActive,
            preservedDistanceFromBottom,
            TerminalOutput.ExtentHeight,
            TerminalOutput.ViewportHeight);
        TerminalScrollHost.ScrollToVerticalOffset(targetOffset);
        if (isAlternateScreenActive)
        {
            TerminalScrollHost.ScrollToHorizontalOffset(0);
        }

        UpdateTerminalChrome();
        return targetOffset;
    }

    private void UpdateFollowOutputState()
    {
        _viewportState.UpdateFollowState(
            _terminalBuffer.IsAlternateScreenActive,
            GetDistanceFromBottom());
        UpdateTerminalChrome();
    }

    private void ApplyAlternateScreenViewportMode(bool isAlternateScreenActive)
    {
        if (!_viewportState.SetAlternateScreenMode(isAlternateScreenActive))
        {
            return;
        }
        ScrollBarVisibility visibility = isAlternateScreenActive
            ? ScrollBarVisibility.Disabled
            : ScrollBarVisibility.Auto;
        TerminalScrollHost.VerticalScrollBarVisibility = visibility;
        TerminalScrollHost.HorizontalScrollBarVisibility = visibility;
        if (isAlternateScreenActive)
        {
            TerminalScrollHost.ScrollToVerticalOffset(0);
            TerminalScrollHost.ScrollToHorizontalOffset(0);
        }
    }

    private void ReplaceTerminalBuffer(AnsiTerminalBuffer nextBuffer)
    {
        nextBuffer.AmbiguousWidthIsWide = _terminalBuffer.AmbiguousWidthIsWide;
        nextBuffer.ApplyColorTheme(_colorTheme);
        _terminalBuffer.InputSequenceGenerated -= TerminalBuffer_InputSequenceGenerated;
        _terminalBuffer.ClipboardSetRequested -= TerminalBuffer_ClipboardSetRequested;
        _terminalBuffer.ClipboardQueryRequested -= TerminalBuffer_ClipboardQueryRequested;
        _terminalBuffer.CurrentDirectoryChanged -= TerminalBuffer_CurrentDirectoryChanged;
        _terminalBuffer.NotificationRequested -= TerminalBuffer_NotificationRequested;
        _terminalBuffer.TaskbarProgressChanged -= TerminalBuffer_TaskbarProgressChanged;
        _terminalBuffer.BellReceived -= TerminalBuffer_BellReceived;
        _terminalBuffer.ShellCommandZoneReceived -= TerminalBuffer_ShellCommandZoneReceived;
        _terminalBuffer.ShellCommandLineReceived -= TerminalBuffer_ShellCommandLineReceived;
        _terminalBuffer.ShellHistoryPathReceived -= TerminalBuffer_ShellHistoryPathReceived;
        _commandNavigation.ResetSession();
        _agentCommands.ResetSession();
        // Command history intentionally survives a restart so the user keeps their history.
        _terminalBuffer = nextBuffer;
        _terminalBuffer.InputSequenceGenerated += TerminalBuffer_InputSequenceGenerated;
        _terminalBuffer.ClipboardSetRequested += TerminalBuffer_ClipboardSetRequested;
        _terminalBuffer.ClipboardQueryRequested += TerminalBuffer_ClipboardQueryRequested;
        _terminalBuffer.CurrentDirectoryChanged += TerminalBuffer_CurrentDirectoryChanged;
        _terminalBuffer.NotificationRequested += TerminalBuffer_NotificationRequested;
        _terminalBuffer.TaskbarProgressChanged += TerminalBuffer_TaskbarProgressChanged;
        _terminalBuffer.BellReceived += TerminalBuffer_BellReceived;
        _terminalBuffer.ShellCommandZoneReceived += TerminalBuffer_ShellCommandZoneReceived;
        _terminalBuffer.ShellCommandLineReceived += TerminalBuffer_ShellCommandLineReceived;
        _terminalBuffer.ShellHistoryPathReceived += TerminalBuffer_ShellHistoryPathReceived;

        // 新しいセッションはタスクバー進捗をクリアした状態から始める。
        SetTaskbarProgress(TaskbarProgressState.None, 0);
    }

    private void TerminalBuffer_ShellHistoryPathReceived(object? sender, string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            _shellHistoryPath = path;
        }
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

    private void TerminalBuffer_CurrentDirectoryChanged(object? sender, string path)
    {
        if (_session is null)
        {
            return;
        }

        string canonicalPath;
        try
        {
            canonicalPath = Path.GetFullPath(path);
        }
        catch
        {
            return;
        }

        _launchState.UpdateActiveWorkingDirectory(canonicalPath);
        _launchState.UpdateWorkingDirectory(canonicalPath, Environment.CurrentDirectory);
        _suppressWorkingDirectoryTextChanged = true;
        WorkingDirectoryTextBox.Text = canonicalPath;
        _suppressWorkingDirectoryTextChanged = false;
        UpdateTerminalChrome();
    }

    private void TerminalBuffer_NotificationRequested(object? sender, string message)
    {
        if (_session is null)
        {
            return;
        }

        ShowToastNotification(message);
    }

    private void TerminalBuffer_TaskbarProgressChanged(object? sender, Terminal.Buffer.TaskbarProgressEventArgs e)
    {
        TaskbarProgressState state = e.State switch
        {
            1 => TaskbarProgressState.Normal,
            2 => TaskbarProgressState.Error,
            3 => TaskbarProgressState.Indeterminate,
            4 => TaskbarProgressState.Warning,
            _ => TaskbarProgressState.None
        };

        // 不確定・解除は進捗値を持たない。
        int progress = state is TaskbarProgressState.Indeterminate or TaskbarProgressState.None ? 0 : e.Progress;
        SetTaskbarProgress(state, progress);
    }

    private void SetTaskbarProgress(TaskbarProgressState state, int progress)
    {
        CurrentTaskbarProgressState = state;
        CurrentTaskbarProgress = progress;
        TaskbarProgressChanged?.Invoke(this, new TaskbarProgressChangedEventArgs(state, progress));
    }

    private void TerminalBuffer_BellReceived(object? sender, EventArgs e)
    {
        // Process はディスパッチャスレッドで実行されるため、そのまま UI 処理してよい。
        HasPendingBell = true;
        PlayBell();
        BellRang?.Invoke(this, EventArgs.Empty);
    }

    // 可聴ベルの再生。将来的に設定でオン/オフできるよう、鳴動処理はこの一箇所に集約する。
    private void PlayBell()
    {
        System.Media.SystemSounds.Beep.Play();
    }

    private void TerminalBuffer_ShellCommandZoneReceived(object? sender, ShellCommandZoneEventArgs e)
    {
        OnAgentShellCommandZone(e);
        RaiseShellCommandActivity(e);
        _commandNavigation.Observe(e.ZoneType, e.AbsoluteLine);

        if (e.ZoneType == ShellCommandZoneType.CommandDone && e.ExitCode.HasValue && e.ExitCode.Value != 0)
        {
            SetStatus($"Command exited with code {e.ExitCode.Value}.");
        }
    }

    private void TerminalBuffer_ShellCommandLineReceived(object? sender, string command)
    {
        RecordCommandHistory(command);
    }

    private void RecordCommandHistory(string command)
    {
        if (!_historyState.Record(command))
        {
            return;
        }

        CommandHistoryRecorded?.Invoke(this, command);

        if (HistoryPopup.IsOpen)
        {
            UpdateHistoryResults();
        }
    }

    private void RaiseShellCommandActivity(ShellCommandZoneEventArgs e)
    {
        if (ShellCommandActivity is not { } handlers)
        {
            return;
        }

        var phase = e.ZoneType switch
        {
            ShellCommandZoneType.PromptStart => ShellCommandPhase.PromptStart,
            ShellCommandZoneType.CommandStart => ShellCommandPhase.CommandStart,
            ShellCommandZoneType.CommandExecuted => ShellCommandPhase.CommandExecuted,
            _ => ShellCommandPhase.CommandDone,
        };
        handlers(this, new ShellCommandActivityEventArgs(phase, e.ExitCode));
    }

    /// <summary>Feeds raw PTY output into the terminal buffer; test seam for marker-driven events.</summary>
    internal void FeedOutputForTests(string data) => _terminalBuffer.Process(data);

    private bool TryScrollToAdjacentCommandLine(bool upward)
    {
        if (!_commandNavigation.HasPrompts)
        {
            return false;
        }

        var (_, charHeight) = MeasureCharacterCell();
        int currentTopLine = (int)(TerminalScrollHost.VerticalOffset / Math.Max(charHeight, 1.0));
        int? targetLine = _commandNavigation.FindAdjacent(currentTopLine, upward);
        if (!targetLine.HasValue)
        {
            return false;
        }

        ScrollToAbsoluteLine(targetLine.Value);
        return true;
    }

    private void ScrollToAbsoluteLine(int absoluteLine)
    {
        var (_, charHeight) = MeasureCharacterCell();
        double offset = absoluteLine * charHeight;
        TerminalScrollHost.ScrollToVerticalOffset(offset);
        _viewportState.StopFollowing();
        UpdateFollowOutputState();
    }

    private void ShowToastNotification(string message)
    {
        _toastDismissTimer.Stop();

        if (_toastPopup is null)
        {
            _toastPopup = new System.Windows.Controls.Primitives.Popup
            {
                Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
                PlacementTarget = this,
                HorizontalOffset = 0,
                VerticalOffset = -70,
                AllowsTransparency = true,
                StaysOpen = true
            };
        }

        _toastPopup.Child = CreateToastBanner(message);
        _toastPopup.IsOpen = true;
        _toastDismissTimer.Start();
    }

    private void ToastDismissTimer_Tick(object? sender, EventArgs e)
    {
        _toastDismissTimer.Stop();
        if (_toastPopup is not null)
        {
            _toastPopup.IsOpen = false;
        }
    }

    private static System.Windows.UIElement CreateToastBanner(string message)
    {
        var border = new System.Windows.Controls.Border
        {
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xE8, 0x1E, 0x1E, 0x1E)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 10, 14, 10),
            MinWidth = 240,
            MaxWidth = 320,
            Child = new System.Windows.Controls.TextBlock
            {
                Text = message,
                Foreground = System.Windows.Media.Brushes.White,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                FontSize = 13
            }
        };
        return border;
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
            _session.Write(_clipboardState.BuildOsc52Response(selectionTargets, text));
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
        TerminalMouseButton button = MapMouseButton(e.ChangedButton);
        if (pressed && button == TerminalMouseButton.Unsupported)
        {
            return false;
        }

        Point position = e.GetPosition(TerminalScrollHost);
        if (_terminalBuffer.MousePixelMode)
        {
            GetMousePixel(position, out int px, out int py);
            return ExecuteMouseAction(_mouseState.ResolveButton(BuildMouseState(), button, pressed, px, py));
        }

        if (!TryGetMouseCell(position, out int column, out int row))
        {
            return false;
        }

        return ExecuteMouseAction(_mouseState.ResolveButton(BuildMouseState(), button, pressed, column, row));
    }

    private bool TrySendMouseMoveEvent(MouseEventArgs e)
    {
        Point position = e.GetPosition(TerminalScrollHost);
        TerminalMouseButton button = ResolveCurrentMouseButton(e);
        if (_terminalBuffer.MousePixelMode)
        {
            GetMousePixel(position, out int px, out int py);
            return ExecuteMouseAction(_mouseState.ResolveMove(BuildMouseState(), button, px, py));
        }

        if (!TryGetMouseCell(position, out int column, out int row))
        {
            return false;
        }

        return ExecuteMouseAction(_mouseState.ResolveMove(BuildMouseState(), button, column, row));
    }

    private bool TrySendMouseWheelEvent(MouseWheelEventArgs e)
    {
        if (_terminalBuffer.MouseTrackingMode == TerminalMouseTrackingMode.Off)
        {
            return ExecuteMouseAction(_mouseState.ResolveWheel(
                BuildMouseState(), e.Delta, 1, 1, Mouse.MouseWheelDeltaForOneLine));
        }

        Point position = e.GetPosition(TerminalScrollHost);
        if (_terminalBuffer.MousePixelMode)
        {
            GetMousePixel(position, out int px, out int py);
            return ExecuteMouseAction(_mouseState.ResolveWheel(
                BuildMouseState(), e.Delta, px, py, Mouse.MouseWheelDeltaForOneLine));
        }

        if (!TryGetMouseCell(position, out int column, out int row))
        {
            return false;
        }

        return ExecuteMouseAction(_mouseState.ResolveWheel(
            BuildMouseState(), e.Delta, column, row, Mouse.MouseWheelDeltaForOneLine));
    }

    private bool ExecuteMouseAction(TerminalMouseAction action)
    {
        if (!action.Handled)
        {
            return false;
        }

        if (action.BytePayload is not null)
        {
            return SendTerminalInput(action.BytePayload);
        }

        return action.TextPayload is not null && SendTerminalInput(action.TextPayload);
    }

    private static TerminalMouseButton MapMouseButton(MouseButton button)
    {
        return button switch
        {
            MouseButton.Left => TerminalMouseButton.Left,
            MouseButton.Middle => TerminalMouseButton.Middle,
            MouseButton.Right => TerminalMouseButton.Right,
            _ => TerminalMouseButton.Unsupported
        };
    }

    private static TerminalMouseButton ResolveCurrentMouseButton(MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            return TerminalMouseButton.Left;
        }

        if (e.MiddleButton == MouseButtonState.Pressed)
        {
            return TerminalMouseButton.Middle;
        }

        if (e.RightButton == MouseButtonState.Pressed)
        {
            return TerminalMouseButton.Right;
        }

        return TerminalMouseButton.None;
    }

    private void TryCaptureTerminalMouse()
    {
        if (_terminalMouseCaptureActive || !_mouseState.ShouldCapture(BuildMouseState()))
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

        if (!_mouseState.ShouldReleaseCapture(force, HasTrackedMouseButtonPressed()))
        {
            return;
        }

        if (ReferenceEquals(Mouse.Captured, TerminalOutput))
        {
            Mouse.Capture(null);
        }

        _terminalMouseCaptureActive = false;
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

    private TerminalMouseState BuildMouseState() => new(
        SupportsTerminalInput(),
        _terminalBuffer.MouseTrackingMode,
        _terminalBuffer.MouseEncoding,
        _terminalBuffer.AlternateScrollEnabled,
        _terminalBuffer.IsAlternateScreenActive,
        BuildAlternateScrollSequence(Key.Up),
        BuildAlternateScrollSequence(Key.Down),
        GetTerminalMouseModifiers());

    private string? BuildAlternateScrollSequence(Key key) =>
        TerminalKeyChordTranslator.TranslateSpecialKey(
            key,
            ModifierKeys.None,
            _terminalBuffer.ApplicationCursorKeysEnabled,
            _terminalBuffer.ModifyOtherKeysLevel,
            _terminalBuffer.KittyKeyboardFlags);

    private static TerminalMouseModifiers GetTerminalMouseModifiers()
    {
        ModifierKeys modifiers = GetTerminalModifiers();
        TerminalMouseModifiers result = TerminalMouseModifiers.None;
        if (modifiers.HasFlag(ModifierKeys.Shift)) result |= TerminalMouseModifiers.Shift;
        if (modifiers.HasFlag(ModifierKeys.Control)) result |= TerminalMouseModifiers.Control;
        if (modifiers.HasFlag(ModifierKeys.Alt)) result |= TerminalMouseModifiers.Alt;
        return result;
    }

    private bool SupportsTerminalInput()
    {
        return _session?.Capabilities.SupportsTerminalInput ?? false;
    }

    private string BuildSessionStartedMessage(string commandLine)
    {
        if (_session is null)
        {
            return $"Started: {commandLine}";
        }

        return $"Started ({_session.Capabilities.DisplayName}): {commandLine}";
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

    private void GetMousePixel(Point position, out int px, out int py)
    {
        double x = Math.Max(0, position.X - TerminalOutput.Padding.Left);
        double y = Math.Max(0, position.Y - TerminalOutput.Padding.Top);
        px = Math.Max(1, (int)Math.Round(x) + 1);
        py = Math.Max(1, (int)Math.Round(y) + 1);
    }

    private (double Width, double Height) MeasureCharacterCell()
    {
        Size size = TerminalOutput.CharacterCellSize;
        return (Math.Max(size.Width, 1.0), Math.Max(size.Height, 1.0));
    }

    internal static bool ShouldRefreshViewportSize(double viewportWidthChange, double viewportHeightChange)
    {
        return IsSignificantViewportChange(viewportWidthChange) ||
            IsSignificantViewportChange(viewportHeightChange);
    }

    private TerminalViewportMetrics GetTerminalViewportMetrics(double? overrideVerticalOffset = null)
    {
        Point viewportOrigin = TerminalScrollHost.TranslatePoint(
            new Point(TerminalOutput.Padding.Left, TerminalOutput.Padding.Top),
            TerminalViewportHost);
        Size scrollViewerViewportSize = ResolveTerminalScrollViewportSize(TerminalOutput.Padding);
        Size viewportSize = TerminalViewportSizing.ResolveViewportSize(
            TerminalOutput.RenderSize,
            TerminalOutput.BorderThickness,
            TerminalOutput.Padding,
            scrollViewerViewportSize);
        double horizontalOffset = TerminalScrollHost.HorizontalOffset;
        // During RenderTerminal the auto-follow scroll is queued but not yet applied, so the
        // ScrollViewer still reports the pre-scroll offset. Callers mid-render pass the resolved
        // target offset so the proxy/IME caret tracks the post-scroll content instead of lagging.
        double verticalOffset = overrideVerticalOffset ?? TerminalScrollHost.VerticalOffset;
        double viewportWidth = viewportSize.Width;
        double viewportHeight = viewportSize.Height;
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

    private Size ResolveTerminalScrollViewportSize(Thickness contentPadding)
    {
        return TerminalViewportSizing.ResolveScrollViewerViewportSize(
            new Size(TerminalScrollHost.ViewportWidth, TerminalScrollHost.ViewportHeight),
            new Size(TerminalScrollHost.ActualWidth, TerminalScrollHost.ActualHeight),
            new Size(TerminalViewportHost.ActualWidth, TerminalViewportHost.ActualHeight),
            contentPadding);
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
        return string.IsNullOrWhiteSpace(_launchState.ActiveCommandLine)
            ? CommandTextBox.Text
            : _launchState.ActiveCommandLine;
    }

    private void SetStatus(string message)
    {
        StatusText.Text = $"[{DateTime.Now:HH:mm:ss}] {message}";
        UpdateTerminalChrome();
    }

    public string CommandLine => _launchState.GetEffectiveCommandLine(TerminalProfileCatalog.BuildDefaultCommandLine());

    public string WorkingDirectory => _launchState.GetEffectiveWorkingDirectory(Environment.CurrentDirectory);

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

    private static bool IsSignificantViewportChange(double change)
    {
        return !double.IsNaN(change) &&
            !double.IsInfinity(change) &&
            Math.Abs(change) > 0.001;
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

        _ = RecoverSessionAsync(isAutomatic: true);
    }

    private async Task RecoverSessionAsync(bool isAutomatic)
    {
        UpdateUiState(_session is not null);
        try
        {
            ITerminalSession? session = _session;
            if (session is null)
            {
                return;
            }

            // Capture the active session's launch parameters before stopping
            string recoveredCommandLine = _launchState.ActiveCommandLine;
            string recoveredWorkingDirectory = _launchState.ActiveWorkingDirectory;

            Task<TerminalRecoveryResult> recovery = _sessionOrchestrator.RecoverAsync(
                session,
                isAutomatic,
                MaxAutoRecoveryAttempts,
                () =>
                {
                    if (!string.IsNullOrEmpty(recoveredCommandLine))
                    {
                        CommandTextBox.Text = recoveredCommandLine;
                    }

                    if (!string.IsNullOrEmpty(recoveredWorkingDirectory))
                    {
                        WorkingDirectoryTextBox.Text = recoveredWorkingDirectory;
                    }

                    SetStatus(isAutomatic
                        ? "Initial output stalled. Unlocking and restarting session..."
                        : "Recover requested. Unlocking and restarting session...");
                },
                () => StartTerminalAsync(focusTerminal: true));
            UpdateUiState(_session is not null);
            TerminalRecoveryResult result = await recovery;

            if (result.Status == TerminalRecoveryStatus.LimitReached)
            {
                SetStatus("Initial output stalled. Click Recover.");
            }
            else if (result.Status == TerminalRecoveryStatus.Failed && result.Error is not null)
            {
                SetStatus($"Recovery failed: {FormatExceptionMessage(result.Error)}");
            }
        }
        finally
        {
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
