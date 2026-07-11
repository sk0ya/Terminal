using Microsoft.Win32;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

using Terminal.Settings;
using Terminal.Sessions;

namespace Terminal.Tabs;

public partial class TerminalTabView
{
    private const double DefaultTerminalFontSize = 14;
    private const double MinTerminalFontSize = 11;
    private const double MaxTerminalFontSize = 24;

    private readonly TerminalLaunchCoordinator _launchState = new(
        TerminalProfileCatalog.CreateProfiles());

    // Maximum scrollback lines for terminal buffers created after the change
    // (the buffer's own limit is readonly, so it applies from the next session).
    private int _scrollbackLimit = TerminalAppSettings.DefaultScrollbackLimit;
    private bool _suppressProfileSelectionChanged;
    private bool _suppressCommandTextChanged;
    private bool _suppressWorkingDirectoryTextChanged;
    private TerminalColorTheme _colorTheme = TerminalColorTheme.Default;

    private readonly TerminalFindCoordinator _findState = new();

    public TerminalColorTheme ColorTheme => _colorTheme;

    public string FontFamilyName => TerminalOutput.FontFamily.Source;

    public double TerminalFontSize => TerminalOutput.FontSize;

    /// <summary>
    /// Whether OpenType programming-font ligatures (e.g. <c>=&gt;</c>, <c>!=</c>, <c>-&gt;</c>) are
    /// rendered. Off by default so the exact one-cell-per-glyph terminal rendering is preserved.
    /// </summary>
    public bool FontLigaturesEnabled => TerminalOutput.FontLigaturesEnabled;

    /// <summary>
    /// Shows or hides the bottom status bar (session chips, status messages
    /// such as non-zero exit codes, and the working directory summary).
    /// </summary>
    public bool IsStatusBarVisible
    {
        get => StatusBarBorder.Visibility == Visibility.Visible;
        set => StatusBarBorder.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Whether OSC 133 shell integration is injected into sessions started
    /// after the change (see <see cref="ShellIntegration"/>; pwsh only).
    /// Running sessions are unaffected until restarted. Marker observation is
    /// reported by <see cref="IsShellIntegrationActive"/>.
    /// </summary>
    public bool ShellIntegrationInjectionEnabled { get; set; } = true;

    public void SetColorTheme(TerminalColorTheme theme)
    {
        ApplyColorTheme(theme);
    }

    public void SetHighContrastMode(bool active)
    {
        TerminalOutput.HighContrastMode = active;
        TerminalOutput.InvalidateVisual();
    }

    public void SetFont(string? fontFamilyName, double fontSize)
    {
        ApplyTerminalFontFamily(fontFamilyName, persist: false);
        ApplyTerminalFontSize(fontSize, persist: false);
    }

    public void SetFontFamily(string? fontFamilyName)
    {
        ApplyTerminalFontFamily(fontFamilyName, persist: false);
    }

    public void SetFontSize(double fontSize)
    {
        ApplyTerminalFontSize(fontSize, persist: false);
    }

    public void SetFontLigaturesEnabled(bool enabled)
    {
        TerminalOutput.FontLigaturesEnabled = enabled;
    }

    private void InitializeTerminalWorkbench()
    {
        WorkingDirectoryTextBox.Text = Environment.CurrentDirectory;
        BuildProfileCatalog();
        ApplySavedWorkbenchSettings();
        ApplyTerminalFontSize(TerminalOutput.FontSize, persist: false);
        UpdateFindMatchCount();
        UpdateTerminalChrome();
    }

    private void BuildProfileCatalog()
    {
        ProfileComboBox.ItemsSource = _launchState.Profiles;
    }

    private void ApplySavedWorkbenchSettings()
    {
        TerminalAppSettings settings = TerminalAppSettings.Load();
        _keyBindings.Update(settings.KeyBindings);
        ApplyColorTheme(TerminalColorThemeCatalog.Resolve(settings));

        string commandLine = string.IsNullOrWhiteSpace(settings.CommandLine)
            ? TerminalProfileCatalog.BuildDefaultCommandLine()
            : settings.CommandLine.Trim();
        string workingDirectory = string.IsNullOrWhiteSpace(settings.WorkingDirectory)
            ? Environment.CurrentDirectory
            : settings.WorkingDirectory.Trim();

        WorkingDirectoryTextBox.Text = workingDirectory;
        ApplyTerminalFontFamily(settings.FontFamilyName, persist: false);
        ApplyTerminalFontSize(settings.FontSize <= 0 ? DefaultTerminalFontSize : settings.FontSize, persist: false);
        SetSelectedProfile(settings.SelectedProfileId, commandLine);
        _terminalBuffer.AmbiguousWidthIsWide = settings.CjkAmbiguousWidthIsWide;
        TerminalOutput.FontLigaturesEnabled = settings.EnableFontLigatures;
        _scrollbackLimit = TerminalAppSettings.ClampScrollbackLimit(settings.ScrollbackLimit);
        IsStatusBarVisible = settings.ShowStatusBar;
        ShellIntegrationInjectionEnabled = settings.EnableShellIntegrationInjection;
    }

    public TerminalAppSettings CreateSettingsSnapshot()
    {
        return new TerminalAppSettings
        {
            SelectedProfileId = _launchState.SelectedProfile.Id,
            CommandLine = _launchState.GetEffectiveCommandLine(TerminalProfileCatalog.BuildDefaultCommandLine()),
            WorkingDirectory = _launchState.GetEffectiveWorkingDirectory(Environment.CurrentDirectory),
            FontFamilyName = TerminalOutput.FontFamily.Source,
            FontSize = TerminalOutput.FontSize,
            CjkAmbiguousWidthIsWide = _terminalBuffer.AmbiguousWidthIsWide,
            EnableFontLigatures = TerminalOutput.FontLigaturesEnabled,
            ScrollbackLimit = _scrollbackLimit
        };
    }

    private void SetSelectedProfile(string? profileId, string commandLine)
    {
        _launchState.Apply(profileId, commandLine, WorkingDirectoryTextBox.Text, Environment.CurrentDirectory);

        _suppressProfileSelectionChanged = true;
        ProfileComboBox.SelectedItem = _launchState.SelectedProfile;
        _suppressProfileSelectionChanged = false;

        _suppressCommandTextChanged = true;
        CommandTextBox.Text = _launchState.CommandLine;
        _suppressCommandTextChanged = false;

        UpdateProfileHint();
    }

    private TerminalProfileDefinition GetSelectedProfile()
    {
        return _launchState.SelectedProfile;
    }

    private void UpdateProfileHint()
    {
        ProfileHintText.Text = _launchState.ProfileHint;
    }

    private bool TryBuildLaunchRequest(out string commandLine, out string workingDirectory)
    {
        if (_launchState.TryBuildLaunchRequest(
            CommandTextBox.Text,
            WorkingDirectoryTextBox.Text,
            TerminalProfileCatalog.BuildDefaultCommandLine(),
            Environment.CurrentDirectory,
            Environment.ExpandEnvironmentVariables,
            Path.GetFullPath,
            Directory.Exists,
            out TerminalLaunchRequest? request,
            out Exception? error))
        {
            commandLine = request!.CommandLine;
            workingDirectory = request.WorkingDirectory;
            WorkingDirectoryTextBox.Text = workingDirectory;
            return true;
        }

        commandLine = string.Empty;
        workingDirectory = string.Empty;
        SetStatus($"Invalid working directory: {error!.Message}");
        return false;
    }

    private void UpdateActiveLaunchState(string commandLine, string workingDirectory)
    {
        _launchState.Activate(commandLine, workingDirectory);
        UpdateTerminalChrome();
    }

    private void ClearActiveLaunchState()
    {
        _launchState.ClearActive(Environment.CurrentDirectory);
        UpdateTerminalChrome();
    }

    private void UpdateTerminalChrome()
    {
        SessionModeValueText.Text = _session is null
            ? "Idle"
            : _session.Capabilities.DisplayName;
        ViewportValueText.Text = $"{_viewportState.Columns}x{_viewportState.Rows}";
        ScrollbackValueText.Text = $"{_terminalBuffer.ScrollbackLineCount} sb / {_terminalBuffer.VisibleLineCount} vis";
        FollowValueText.Text = _viewportState.FollowOutput ? "Follow" : "Pinned";
        FontSizeValueText.Text = $"{TerminalOutput.FontSize:0}px";

        string workingDirectory = _session is null
            ? (string.IsNullOrWhiteSpace(WorkingDirectoryTextBox.Text) ? Environment.CurrentDirectory : WorkingDirectoryTextBox.Text.Trim())
            : _launchState.ActiveWorkingDirectory;
        WorkingDirectorySummaryText.Text = workingDirectory;
    }

    private void ProfileComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressProfileSelectionChanged)
        {
            return;
        }

        string commandLine = _launchState.SelectProfile(ProfileComboBox.SelectedItem as TerminalProfileDefinition);
        _suppressCommandTextChanged = true;
        CommandTextBox.Text = commandLine;
        _suppressCommandTextChanged = false;

        UpdateProfileHint();
        UpdateTerminalChrome();
        UpdateWindowTitle();
    }

    private void CommandTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressCommandTextChanged)
        {
            return;
        }

        TerminalProfileDefinition matchedProfile = _launchState.UpdateCommandLine(CommandTextBox.Text);
        _suppressProfileSelectionChanged = true;
        ProfileComboBox.SelectedItem = matchedProfile;
        _suppressProfileSelectionChanged = false;
        UpdateProfileHint();
        UpdateWindowTitle();
    }

    private void WorkingDirectoryTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressWorkingDirectoryTextChanged)
        {
            return;
        }

        _launchState.UpdateWorkingDirectory(WorkingDirectoryTextBox.Text, Environment.CurrentDirectory);
        UpdateTerminalChrome();
    }

    private void WorkingDirectoryHereButton_Click(object sender, RoutedEventArgs e)
    {
        WorkingDirectoryTextBox.Text = Environment.CurrentDirectory;
        SetStatus("Working directory reset to the current process directory.");
    }

    private async void RestartButton_Click(object sender, RoutedEventArgs e)
    {
        _sessionOrchestrator.ResetRecoveryAttempts();
        await StartTerminalAsync(focusTerminal: true);
    }

    private void SaveTranscriptButton_Click(object sender, RoutedEventArgs e)
    {
        SaveTranscript();
    }

    private void FindToggleButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleFindPanel();
    }

    private void FindTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        TerminalFindKeyAction action = TerminalFindCoordinator.ResolveKey(
            MapFindKey(GetEffectiveKey(e)),
            MapFindKeyModifiers(Keyboard.Modifiers));
        switch (action.Kind)
        {
            case TerminalFindKeyActionKind.Close:
                CloseFindPanel();
                break;
            case TerminalFindKeyActionKind.Move:
                MoveFind(action.Forward);
                break;
            case TerminalFindKeyActionKind.ToggleCaseSensitivity:
                FindCaseSensitiveCheckBox.IsChecked = FindCaseSensitiveCheckBox.IsChecked != true;
                RefreshFind(reseek: true);
                break;
        }

        if (action.Handled)
        {
            e.Handled = true;
        }
    }

    private static TerminalFindKey MapFindKey(Key key) => key switch
    {
        Key.Escape => TerminalFindKey.Escape,
        Key.Enter => TerminalFindKey.Enter,
        Key.F3 => TerminalFindKey.F3,
        Key.C => TerminalFindKey.C,
        _ => TerminalFindKey.Other
    };

    private static TerminalFindKeyModifiers MapFindKeyModifiers(ModifierKeys modifiers)
    {
        TerminalFindKeyModifiers result = TerminalFindKeyModifiers.None;
        if ((modifiers & ModifierKeys.Shift) != 0) result |= TerminalFindKeyModifiers.Shift;
        if ((modifiers & ModifierKeys.Alt) != 0) result |= TerminalFindKeyModifiers.Alt;
        if ((modifiers & ModifierKeys.Control) != 0) result |= TerminalFindKeyModifiers.Control;
        if ((modifiers & ModifierKeys.Windows) != 0) result |= TerminalFindKeyModifiers.Windows;
        return result;
    }

    private void FindTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (FindPopup.IsOpen)
        {
            RefreshFind(reseek: true);
        }
    }

    private void FindOptions_Changed(object sender, RoutedEventArgs e)
    {
        RefreshFind(reseek: true);
    }

    private void ZoomOutButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyTerminalFontSize(TerminalOutput.FontSize - 1);
    }

    private void ZoomResetButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyTerminalFontSize(DefaultTerminalFontSize);
    }

    private void ZoomInButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyTerminalFontSize(TerminalOutput.FontSize + 1);
    }

    private void LaunchInput_KeyDown(object sender, KeyEventArgs e)
    {
        TerminalLaunchInputAction action = TerminalLaunchCoordinator.ResolveInput(
            () => MapLaunchInputKey(GetEffectiveKey(e)),
            _session is not null,
            _isSessionTransitionActive,
            _isRecovering,
            _isClosingWindow);
        if (action != TerminalLaunchInputAction.Start)
        {
            return;
        }

        e.Handled = true;
        StartButton_Click(StartButton, new RoutedEventArgs(Button.ClickEvent));
    }

    private static TerminalLaunchInputKey MapLaunchInputKey(Key key) =>
        key == Key.Enter ? TerminalLaunchInputKey.Enter : TerminalLaunchInputKey.Other;

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        ModifierKeys modifiers = Keyboard.Modifiers;
        Key key = GetEffectiveKey(e);

        TerminalWorkbenchShortcutAction shortcutAction = ResolveConfiguredWorkbenchShortcut(key, modifiers);
        switch (shortcutAction)
        {
            case TerminalWorkbenchShortcutAction.SaveTranscript:
                SaveTranscript();
                break;
            case TerminalWorkbenchShortcutAction.Restart:
                RestartButton_Click(RestartButton, new RoutedEventArgs(Button.ClickEvent));
                break;
            case TerminalWorkbenchShortcutAction.IncreaseFontSize:
                ApplyTerminalFontSize(TerminalOutput.FontSize + 1);
                break;
            case TerminalWorkbenchShortcutAction.DecreaseFontSize:
                ApplyTerminalFontSize(TerminalOutput.FontSize - 1);
                break;
            case TerminalWorkbenchShortcutAction.ResetFontSize:
                ApplyTerminalFontSize(DefaultTerminalFontSize);
                break;
        }

        if (shortcutAction != TerminalWorkbenchShortcutAction.None)
        {
            e.Handled = true;
            return;
        }

        TerminalFindKeyAction findAction = TerminalFindCoordinator.ResolveWindowKey(
            MapFindKey(key),
            MapFindKeyModifiers(modifiers),
            FindPopup.IsOpen);
        switch (findAction.Kind)
        {
            case TerminalFindKeyActionKind.Move:
                MoveFind(findAction.Forward);
                break;
            case TerminalFindKeyActionKind.Close:
                CloseFindPanel();
                break;
        }

        if (findAction.Handled)
        {
            e.Handled = true;
        }
    }

    private TerminalWorkbenchShortcutAction ResolveConfiguredWorkbenchShortcut(Key key, ModifierKeys modifiers)
    {
        if (_keyBindings.Matches("SaveTranscript", key, modifiers)) return TerminalWorkbenchShortcutAction.SaveTranscript;
        if (_keyBindings.Matches("Restart", key, modifiers)) return TerminalWorkbenchShortcutAction.Restart;
        if (_keyBindings.Matches("IncreaseFontSize", key, modifiers)) return TerminalWorkbenchShortcutAction.IncreaseFontSize;
        if (_keyBindings.Matches("DecreaseFontSize", key, modifiers)) return TerminalWorkbenchShortcutAction.DecreaseFontSize;
        if (_keyBindings.Matches("ResetFontSize", key, modifiers)) return TerminalWorkbenchShortcutAction.ResetFontSize;
        return TerminalWorkbenchShortcutAction.None;
    }

    private static TerminalWorkbenchShortcutKey MapWorkbenchShortcutKey(Key key) => key switch
    {
        Key.S => TerminalWorkbenchShortcutKey.S,
        Key.R => TerminalWorkbenchShortcutKey.R,
        Key.Add => TerminalWorkbenchShortcutKey.Add,
        Key.OemPlus => TerminalWorkbenchShortcutKey.OemPlus,
        Key.Subtract => TerminalWorkbenchShortcutKey.Subtract,
        Key.OemMinus => TerminalWorkbenchShortcutKey.OemMinus,
        Key.D0 => TerminalWorkbenchShortcutKey.D0,
        Key.NumPad0 => TerminalWorkbenchShortcutKey.NumPad0,
        _ => TerminalWorkbenchShortcutKey.Other
    };

    private static TerminalWorkbenchShortcutModifiers MapWorkbenchShortcutModifiers(ModifierKeys modifiers)
    {
        TerminalWorkbenchShortcutModifiers result = TerminalWorkbenchShortcutModifiers.None;
        if ((modifiers & ModifierKeys.Shift) != 0)
        {
            result |= TerminalWorkbenchShortcutModifiers.Shift;
        }
        if ((modifiers & ModifierKeys.Control) != 0)
        {
            result |= TerminalWorkbenchShortcutModifiers.Control;
        }
        if ((modifiers & ModifierKeys.Alt) != 0) result |= TerminalWorkbenchShortcutModifiers.Alt;
        if ((modifiers & ModifierKeys.Windows) != 0) result |= TerminalWorkbenchShortcutModifiers.Windows;
        return result;
    }

    private void ApplyTerminalFontSize(double fontSize, bool persist = true)
    {
        double clamped = Math.Round(Math.Clamp(fontSize, MinTerminalFontSize, MaxTerminalFontSize));
        TerminalOutput.FontSize = clamped;
        TerminalInputProxy.FontSize = clamped;
        UpdateTerminalChrome();
        RequestDocumentRender(immediate: true);
        QueueTerminalViewportSizeUpdate();
    }

    private void ApplyTerminalFontFamily(string? fontFamilyName, bool persist = true)
    {
        string normalized = TerminalFontCatalog.NormalizeFontFamilyName(fontFamilyName);
        if (!string.Equals(TerminalOutput.FontFamily.Source, normalized, StringComparison.Ordinal))
        {
            FontFamily fontFamily = TerminalFontCatalog.CreateFontFamily(normalized);
            TerminalOutput.FontFamily = fontFamily;
            TerminalInputProxy.FontFamily = fontFamily;
        }

        UpdateTerminalChrome();
        RequestDocumentRender(immediate: true);
        QueueTerminalViewportSizeUpdate();
    }

    private void ApplyColorTheme(TerminalColorTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);

        _colorTheme = theme;
        _terminalBuffer.ApplyColorTheme(theme);
        Brush background = CreateFrozenBrush(theme.Background);
        Brush foreground = CreateFrozenBrush(theme.Foreground);
        Brush selectionBackground = CreateFrozenBrush(theme.SelectionBackground);

        TerminalHostBorder.Background = background;
        TerminalViewportHost.Background = background;
        TerminalScrollHost.Background = background;
        TerminalOutput.Background = background;
        TerminalOutput.Foreground = foreground;
        TerminalOutput.SelectionBackground = selectionBackground;
        TerminalInputProxy.Foreground = foreground;

        ApplyHistoryPopupTheme(theme);

        RequestDocumentRender(immediate: true);
    }

    /// <summary>
    /// Recolors the Ctrl+R history popup from the active theme by overwriting the
    /// HistoryPopup* dynamic resources. The selected-row colour is the theme's
    /// selection colour, and the accent (prompt / pointer / match highlight /
    /// caret) is a brightened variant of it, so a single theme change restyles
    /// both the terminal and the popup while keeping the accent legible on the
    /// selected row.
    /// </summary>
    private void ApplyHistoryPopupTheme(TerminalColorTheme theme)
    {
        Color selection = theme.SelectionBackground;
        // Brighten the selection hue toward the foreground for the accent
        // (match highlight / pointer / caret). If the accent matched the
        // selection background exactly, matched characters became invisible on
        // the auto-selected row, whose background is that same selection colour.
        Color accent = Blend(selection, theme.Foreground, 0.55);

        Resources["HistoryPopupBackgroundBrush"] = CreateFrozenBrush(Blend(theme.Background, theme.Foreground, 0.06));
        Resources["HistoryPopupBorderBrush"] = CreateFrozenBrush(Blend(theme.Background, theme.Foreground, 0.30));
        Resources["HistoryPopupForegroundBrush"] = CreateFrozenBrush(theme.Foreground);
        Resources["HistoryPopupAccentBrush"] = CreateFrozenBrush(accent);
        Resources["HistoryPopupSelectionBrush"] = CreateFrozenBrush(selection);
        Resources["HistoryPopupCountBrush"] = CreateFrozenBrush(WithAlpha(theme.Foreground, 0x99));
    }

    private static Color Blend(Color from, Color to, double amount)
    {
        amount = Math.Clamp(amount, 0.0, 1.0);
        return Color.FromRgb(
            (byte)Math.Round(from.R + ((to.R - from.R) * amount)),
            (byte)Math.Round(from.G + ((to.G - from.G) * amount)),
            (byte)Math.Round(from.B + ((to.B - from.B) * amount)));
    }

    private static Color WithAlpha(Color color, byte alpha)
        => Color.FromArgb(alpha, color.R, color.G, color.B);

    public void ApplySettings(TerminalAppSettings settings)
    {
        _keyBindings.Update(settings.KeyBindings);
        ApplyColorTheme(TerminalColorThemeCatalog.Resolve(settings));
        string commandLine = string.IsNullOrWhiteSpace(settings.CommandLine)
            ? TerminalProfileCatalog.BuildDefaultCommandLine()
            : settings.CommandLine.Trim();
        string workingDirectory = string.IsNullOrWhiteSpace(settings.WorkingDirectory)
            ? Environment.CurrentDirectory
            : settings.WorkingDirectory.Trim();

        WorkingDirectoryTextBox.Text = workingDirectory;
        ApplyTerminalFontFamily(settings.FontFamilyName, persist: false);
        ApplyTerminalFontSize(settings.FontSize <= 0 ? DefaultTerminalFontSize : settings.FontSize, persist: false);
        SetSelectedProfile(settings.SelectedProfileId, commandLine);
        _terminalBuffer.AmbiguousWidthIsWide = settings.CjkAmbiguousWidthIsWide;
        TerminalOutput.FontLigaturesEnabled = settings.EnableFontLigatures;
        _scrollbackLimit = TerminalAppSettings.ClampScrollbackLimit(settings.ScrollbackLimit);
        IsStatusBarVisible = settings.ShowStatusBar;
        ShellIntegrationInjectionEnabled = settings.EnableShellIntegrationInjection;
        UpdateWindowTitle();
    }

    public void SetBackdropActive(bool active)
    {
        var transparent = new SolidColorBrush(Colors.Transparent);
        var opaque = new SolidColorBrush(_colorTheme.Background);
        Brush bg = active ? transparent : opaque;
        TerminalHostBorder.Background = bg;
        TerminalViewportHost.Background = bg;
        TerminalScrollHost.Background = bg;
        TerminalOutput.Background = bg;
    }

    private void ToggleFindPanel()
    {
        if (FindPopup.IsOpen)
        {
            CloseFindPanel();
        }
        else
        {
            OpenFindPanel();
        }
    }

    private void OpenFindPanel()
    {
        CloseHistoryPanel();
        // 開くたびに起点を先頭へ戻し、既存の検索語があれば近い一致を選び直す。
        _findState.Open();
        FindPopup.IsOpen = true;
        RefreshFind(reseek: true);
    }

    private void CloseFindPanel()
    {
        if (!FindPopup.IsOpen)
        {
            return;
        }

        FindPopup.IsOpen = false;
    }

    private void FindPopup_Opened(object sender, EventArgs e)
    {
        // Popup はメインのビジュアルツリー外なので、表示後に明示的にフォーカスを移す。
        FindTextBox.Focus();
        FindTextBox.SelectAll();
        Keyboard.Focus(FindTextBox);
    }

    private void FindPopup_Closed(object sender, EventArgs e)
    {
        // 閉じたら検索ハイライト（選択）を消し、ターミナルへフォーカスを戻す。
        _findState.Close();
        TerminalOutput.ClearSelection();
        FindCountText.Text = "Type to search";
        if (_session is not null)
        {
            FocusTerminalInput();
        }
    }

    private IReadOnlyList<TerminalMatch> FindSurfaceMatches()
        => TerminalOutput.FindMatches(_findState.Query, _findState.Comparison);

    // 検索語・オプション変更時に呼ぶ。一致を作り直し、reseek=true なら起点に近い一致を、そうでな
    // ければ現在インデックスを維持して現在一致を選び直す。空検索語・不一致はカウント表示のみ更新。
    private void RefreshFind(bool reseek)
    {
        if (!FindPopup.IsOpen)
        {
            return;
        }

        if (!_findState.UpdateCriteria(
                FindTextBox.Text,
                FindCaseSensitiveCheckBox.IsChecked == true))
        {
            TerminalOutput.ClearSelection();
            FindCountText.Text = _findState.PositionText;
            return;
        }

        _findState.Refresh(FindSurfaceMatches(), reseek);
        if (_findState.Status == TerminalFindStatus.NoMatch)
        {
            TerminalOutput.ClearSelection();
            FindCountText.Text = _findState.PositionText;
            return;
        }

        ApplyCurrentFindMatch();
    }

    // Enter / F3（＋Shift）やナビゲーションボタンからの「次/前」移動。
    private void MoveFind(bool forward)
    {
        if (!_findState.UpdateCriteria(
                FindTextBox.Text,
                FindCaseSensitiveCheckBox.IsChecked == true))
        {
            TerminalOutput.ClearSelection();
            FindCountText.Text = _findState.PositionText;
            return;
        }

        _findState.Move(FindSurfaceMatches(), forward);
        if (_findState.Status == TerminalFindStatus.NoMatch)
        {
            TerminalOutput.ClearSelection();
            FindCountText.Text = _findState.PositionText;
            return;
        }

        ApplyCurrentFindMatch();
    }

    // 現在インデックスの一致を選択ハイライトし、可視範囲へスクロールしてカウント表示を更新する。
    // 選択ハイライト＋スクロール経路（TerminalSurfaceControl.SelectMatch）を再利用する。
    private void ApplyCurrentFindMatch()
    {
        if (_findState.CurrentMatch is not TerminalMatch match)
        {
            return;
        }

        TerminalOutput.SelectMatch(match.LineIndex, match.Column, match.Length);
        _findState.MarkCurrentMatchApplied();
        FindCountText.Text = _findState.PositionText;
    }

    /// <summary>
    /// 外部UI（ホストアプリのコマンドパレット等）からターミナル内テキスト検索を駆動するための公開口。
    /// 組み込みの検索パネル（Ctrl+Shift+F）と同じ <see cref="TerminalSurfaceControl.TrySelectNextMatch"/> を用い、
    /// 一致箇所を選択ハイライトしてそこへスクロールする。検索パネルの表示状態には影響しない。
    /// </summary>
    /// <param name="query">検索文字列。空・空白なら何もせず false を返す。</param>
    /// <param name="forward"><c>true</c>=順方向（次へ）/ <c>false</c>=逆方向（前へ）。</param>
    /// <param name="caseSensitive">大文字小文字を区別するか。</param>
    /// <param name="wrapped">末尾／先頭で折り返してヒットしたとき <c>true</c>。</param>
    /// <returns>一致が見つかれば <c>true</c>。</returns>
    public bool FindInTerminal(string query, bool forward, bool caseSensitive, out bool wrapped)
    {
        wrapped = false;
        if (string.IsNullOrWhiteSpace(query))
        {
            return false;
        }

        StringComparison comparison = caseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        return TerminalOutput.TrySelectNextMatch(query, comparison, forward, out wrapped);
    }

    /// <summary>
    /// ターミナルバッファ内の <paramref name="query"/> 一致をすべて列挙する。ホストアプリ
    /// （コマンドパレット等）が一致一覧を提示するための公開口。選択状態・スクロール位置は変えない。
    /// </summary>
    /// <param name="query">検索文字列。空・空白なら空配列を返す。</param>
    /// <param name="caseSensitive">大文字小文字を区別するか。</param>
    public IReadOnlyList<TerminalMatch> FindMatches(string query, bool caseSensitive)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<TerminalMatch>();
        }

        StringComparison comparison = caseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        return TerminalOutput.FindMatches(query, comparison);
    }

    /// <summary>
    /// <see cref="FindMatches"/> で得た一致を選択ハイライトし、その箇所までスクロールして可視化する。
    /// </summary>
    /// <returns>選択できれば <c>true</c>。</returns>
    public bool SelectMatch(TerminalMatch match)
        => TerminalOutput.SelectMatch(match.LineIndex, match.Column, match.Length);

    // ターミナル出力が流れて行が変化するたび（RenderTerminal から）呼ばれ、検索バーが開いていれば
    // 一致一覧とカウント表示を追従させる。現在ハイライトは動かさない（自動追従スクロールと競合させ
    // ないため）よう、インデックスはクランプのみで再スクロールしない。
    private void UpdateFindMatchCount()
    {
        if (!FindPopup.IsOpen)
        {
            return;
        }

        if (!_findState.UpdateCriteria(
                FindTextBox.Text,
                FindCaseSensitiveCheckBox.IsChecked == true))
        {
            FindCountText.Text = _findState.PositionText;
            return;
        }

        _findState.RefreshAfterOutputChange(FindSurfaceMatches());
        FindCountText.Text = _findState.PositionText;
    }

    private void OpenHistoryPanel()
    {
        CloseFindPanel();
        EnsureHistorySeeded();
        HistorySearchBox.Clear();
        HistoryPopup.IsOpen = true;
        UpdateHistoryResults();
    }

    private void EnsureHistorySeeded()
    {
        _historyState.SeedOnce(PSReadLineHistorySeedingEnabled, () =>
        {
            string? path = ResolvePSReadLineHistoryPath();
            return path is null ? [] : PSReadLineHistory.Read(path);
        });
    }

    private string? ResolvePSReadLineHistoryPath()
    {
        // The shell reports its exact HistorySavePath via OSC 633;P when shell
        // integration is active; that is authoritative (handles custom paths).
        string commandLine = _launchState.GetActiveCommandLineOr(_initialCommandLine);
        // Otherwise only guess for PowerShell shells, probing the known defaults.
        return TerminalHistorySeedResolver.ResolvePath(
            _shellHistoryPath,
            commandLine,
            File.Exists,
            PSReadLineHistory.FindDefaultHistoryPath);
    }

    private void CloseHistoryPanel()
    {
        if (!HistoryPopup.IsOpen)
        {
            return;
        }

        HistoryPopup.IsOpen = false;
        HistoryResults.Items.Clear();
        if (_session is not null)
        {
            FocusTerminalInput();
        }
    }

    private void HistoryPopup_Opened(object sender, EventArgs e)
    {
        // The popup lives outside the main visual tree, so move keyboard focus
        // explicitly once it is shown.
        HistorySearchBox.Focus();
        Keyboard.Focus(HistorySearchBox);
    }

    private void UpdateHistoryResults()
    {
        _historyState.Search(HistorySearchBox.Text);
        HistoryResults.Items.Clear();
        foreach (TerminalHistoryResult result in _historyState.Results)
        {
            HistoryResults.Items.Add(BuildHistoryItem(
                result.Command,
                result.Display,
                result.MatchedIndices));
        }

        HistoryResults.SelectedIndex = _historyState.SelectedIndex;
        if (_historyState.SelectedIndex >= 0)
        {
            HistoryResults.ScrollIntoView(HistoryResults.SelectedItem);
        }

        HistoryCountText.Text = _historyState.CountText;
    }

    private TextBlock BuildHistoryItem(string command, string display, IReadOnlyList<int> matchedIndices)
    {
        var block = new TextBlock
        {
            Tag = command,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        if (matchedIndices.Count == 0)
        {
            block.Inlines.Add(new Run(display));
            return block;
        }

        Brush highlight = TryFindResource("HistoryPopupAccentBrush") as Brush ?? Brushes.Orange;
        foreach (TerminalHistoryDisplaySegment segment in
                 TerminalHistoryCoordinator.BuildDisplaySegments(display, matchedIndices))
        {
            var run = new Run(segment.Text);
            if (segment.Highlighted)
            {
                run.Foreground = highlight;
                run.FontWeight = FontWeights.Bold;
            }

            block.Inlines.Add(run);
        }

        return block;
    }

    /// <summary>
    /// fzf-style fuzzy match: every character of <paramref name="query"/> must
    /// appear in <paramref name="text"/> in order (case-insensitive). Scores
    /// higher for matches that are consecutive, at the start, or after a word
    /// boundary. Returns the matched character indices for highlighting.
    /// </summary>
    internal static bool TryFuzzyMatch(string text, string query, out int score, out IReadOnlyList<int> matchedIndices)
    {
        return TerminalHistoryCoordinator.TryFuzzyMatch(
            text,
            query,
            out score,
            out matchedIndices);
    }

    private void HistorySearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (HistoryPopup.IsOpen)
        {
            UpdateHistoryResults();
        }
    }

    private void HistorySearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        TerminalHistoryKeyAction action = TerminalHistoryCoordinator.ResolveKey(
            MapHistoryKey(e.Key),
            MapHistoryKeyModifiers(Keyboard.Modifiers));
        switch (action.Kind)
        {
            case TerminalHistoryKeyActionKind.Close:
                CloseHistoryPanel();
                break;
            case TerminalHistoryKeyActionKind.Accept:
                AcceptHistorySelection();
                break;
            case TerminalHistoryKeyActionKind.MoveSelection:
                MoveHistorySelection(action.SelectionDelta);
                break;
        }

        if (action.Handled)
        {
            e.Handled = true;
        }
    }

    private static TerminalHistoryKey MapHistoryKey(Key key) => key switch
    {
        Key.Escape => TerminalHistoryKey.Escape,
        Key.Enter => TerminalHistoryKey.Enter,
        Key.Up => TerminalHistoryKey.Up,
        Key.Down => TerminalHistoryKey.Down,
        Key.N => TerminalHistoryKey.N,
        Key.P => TerminalHistoryKey.P,
        Key.R => TerminalHistoryKey.R,
        _ => TerminalHistoryKey.Other
    };

    private static TerminalHistoryKeyModifiers MapHistoryKeyModifiers(ModifierKeys modifiers)
    {
        TerminalHistoryKeyModifiers result = TerminalHistoryKeyModifiers.None;
        if ((modifiers & ModifierKeys.Control) != 0) result |= TerminalHistoryKeyModifiers.Control;
        if ((modifiers & ModifierKeys.Shift) != 0) result |= TerminalHistoryKeyModifiers.Shift;
        if ((modifiers & ModifierKeys.Alt) != 0) result |= TerminalHistoryKeyModifiers.Alt;
        if ((modifiers & ModifierKeys.Windows) != 0) result |= TerminalHistoryKeyModifiers.Windows;
        return result;
    }

    private void MoveHistorySelection(int delta)
    {
        _historyState.SelectIndex(HistoryResults.SelectedIndex);
        int next = _historyState.MoveSelection(delta);
        if (next < 0)
        {
            return;
        }

        HistoryResults.SelectedIndex = next;
        HistoryResults.ScrollIntoView(HistoryResults.SelectedItem);
    }

    private void HistoryResults_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        AcceptHistorySelection();
    }

    private void AcceptHistorySelection()
    {
        _historyState.SelectIndex(HistoryResults.SelectedIndex);
        string? command = _historyState.AcceptSelection();
        if (command is null)
        {
            return;
        }

        CloseHistoryPanel();
        // Insert into the shell's input line only; the user reviews and submits.
        SendTerminalInput(command);
    }

    private void SaveTranscript()
    {
        string transcript = _terminalBuffer.CreatePlainTextSnapshot();
        var dialog = new SaveFileDialog
        {
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = _launchState.BuildTranscriptFileName(
                _terminalBuffer.WindowTitle,
                () => GetSelectedProfile().DisplayName,
                () => DateTime.Now)
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        File.WriteAllText(dialog.FileName, transcript, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        SetStatus($"Saved transcript: {dialog.FileName}");
    }

}
