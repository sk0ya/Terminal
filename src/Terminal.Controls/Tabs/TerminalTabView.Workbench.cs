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

    private readonly List<TerminalProfileDefinition> _profiles = [];
    private readonly TerminalProfileDefinition _customProfile = new(
        "custom",
        "Custom",
        string.Empty,
        "Use any executable or shell command line.",
        IsCustom: true);

    private string _activeCommandLine = string.Empty;
    private string _activeWorkingDirectory = Environment.CurrentDirectory;
    private bool _suppressProfileSelectionChanged;
    private bool _suppressCommandTextChanged;
    private bool _suppressWorkingDirectoryTextChanged;
    private TerminalColorTheme _colorTheme = TerminalColorTheme.Default;

    public TerminalColorTheme ColorTheme => _colorTheme;

    public string FontFamilyName => TerminalOutput.FontFamily.Source;

    public double TerminalFontSize => TerminalOutput.FontSize;

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
        _profiles.Clear();
        _profiles.AddRange(TerminalProfileCatalog.CreateProfiles());
        _profiles.Add(_customProfile);
        ProfileComboBox.ItemsSource = _profiles;
    }

    private void ApplySavedWorkbenchSettings()
    {
        TerminalAppSettings settings = TerminalAppSettings.Load();

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
        IsStatusBarVisible = settings.ShowStatusBar;
        ShellIntegrationInjectionEnabled = settings.EnableShellIntegrationInjection;
    }

    public TerminalAppSettings CreateSettingsSnapshot()
    {
        return new TerminalAppSettings
        {
            SelectedProfileId = GetSelectedProfile().Id,
            CommandLine = string.IsNullOrWhiteSpace(CommandTextBox.Text)
                ? TerminalProfileCatalog.BuildDefaultCommandLine()
                : CommandTextBox.Text.Trim(),
            WorkingDirectory = string.IsNullOrWhiteSpace(WorkingDirectoryTextBox.Text)
                ? Environment.CurrentDirectory
                : WorkingDirectoryTextBox.Text.Trim(),
            FontFamilyName = TerminalOutput.FontFamily.Source,
            FontSize = TerminalOutput.FontSize,
            CjkAmbiguousWidthIsWide = _terminalBuffer.AmbiguousWidthIsWide
        };
    }

    private void SetSelectedProfile(string? profileId, string commandLine)
    {
        TerminalProfileDefinition selectedProfile = TerminalProfileCatalog.ResolveSelectedProfile(
            _profiles,
            _customProfile,
            profileId,
            commandLine);

        string effectiveCommandLine = string.IsNullOrWhiteSpace(commandLine) && !selectedProfile.IsCustom
            ? selectedProfile.CommandLine
            : commandLine;

        _suppressProfileSelectionChanged = true;
        ProfileComboBox.SelectedItem = selectedProfile;
        _suppressProfileSelectionChanged = false;

        _suppressCommandTextChanged = true;
        CommandTextBox.Text = effectiveCommandLine;
        _suppressCommandTextChanged = false;

        UpdateProfileHint();
    }

    private TerminalProfileDefinition GetSelectedProfile()
    {
        return ProfileComboBox.SelectedItem as TerminalProfileDefinition ?? _customProfile;
    }

    private TerminalProfileDefinition? MatchProfileByCommandLine(string? commandLine)
    {
        return TerminalProfileCatalog.MatchProfileByCommandLine(_profiles, commandLine);
    }

    private void UpdateProfileHint()
    {
        TerminalProfileDefinition profile = GetSelectedProfile();
        ProfileHintText.Text = profile.Description;
    }

    private bool TryBuildLaunchRequest(out string commandLine, out string workingDirectory)
    {
        commandLine = string.IsNullOrWhiteSpace(CommandTextBox.Text)
            ? TerminalProfileCatalog.BuildDefaultCommandLine()
            : CommandTextBox.Text.Trim();

        try
        {
            workingDirectory = NormalizeWorkingDirectory(WorkingDirectoryTextBox.Text);
            WorkingDirectoryTextBox.Text = workingDirectory;
            return true;
        }
        catch (Exception ex)
        {
            workingDirectory = string.Empty;
            SetStatus($"Invalid working directory: {ex.Message}");
            return false;
        }
    }

    private static string NormalizeWorkingDirectory(string? rawPath)
    {
        string candidate = string.IsNullOrWhiteSpace(rawPath)
            ? Environment.CurrentDirectory
            : Environment.ExpandEnvironmentVariables(rawPath.Trim());
        string fullPath = Path.GetFullPath(candidate);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException(fullPath);
        }

        return fullPath;
    }

    private void UpdateActiveLaunchState(string commandLine, string workingDirectory)
    {
        _activeCommandLine = commandLine;
        _activeWorkingDirectory = workingDirectory;
        UpdateTerminalChrome();
    }

    private void ClearActiveLaunchState()
    {
        _activeCommandLine = string.Empty;
        _activeWorkingDirectory = Environment.CurrentDirectory;
        UpdateTerminalChrome();
    }

    private void UpdateTerminalChrome()
    {
        SessionModeValueText.Text = _session is null
            ? "Idle"
            : _session.Capabilities.DisplayName;
        ViewportValueText.Text = $"{_currentColumns}x{_currentRows}";
        ScrollbackValueText.Text = $"{_terminalBuffer.ScrollbackLineCount} sb / {_terminalBuffer.VisibleLineCount} vis";
        FollowValueText.Text = _followTerminalOutput ? "Follow" : "Pinned";
        FontSizeValueText.Text = $"{TerminalOutput.FontSize:0}px";

        string workingDirectory = _session is null
            ? (string.IsNullOrWhiteSpace(WorkingDirectoryTextBox.Text) ? Environment.CurrentDirectory : WorkingDirectoryTextBox.Text.Trim())
            : _activeWorkingDirectory;
        WorkingDirectorySummaryText.Text = workingDirectory;
    }

    private void ProfileComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressProfileSelectionChanged)
        {
            return;
        }

        TerminalProfileDefinition profile = GetSelectedProfile();
        if (!profile.IsCustom && !string.IsNullOrWhiteSpace(profile.CommandLine))
        {
            _suppressCommandTextChanged = true;
            CommandTextBox.Text = profile.CommandLine;
            _suppressCommandTextChanged = false;
        }

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

        TerminalProfileDefinition matchedProfile = MatchProfileByCommandLine(CommandTextBox.Text) ?? _customProfile;
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

        UpdateTerminalChrome();
    }

    private void WorkingDirectoryHereButton_Click(object sender, RoutedEventArgs e)
    {
        WorkingDirectoryTextBox.Text = Environment.CurrentDirectory;
        SetStatus("Working directory reset to the current process directory.");
    }

    private async void RestartButton_Click(object sender, RoutedEventArgs e)
    {
        _autoRecoveryAttempts = 0;
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

    private void CloseFindButton_Click(object sender, RoutedEventArgs e)
    {
        CloseFindPanel();
    }

    private void FindNextButton_Click(object sender, RoutedEventArgs e)
    {
        _ = TryFindInTerminal(forward: true);
    }

    private void FindPreviousButton_Click(object sender, RoutedEventArgs e)
    {
        _ = TryFindInTerminal(forward: false);
    }

    private void FindTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        Key key = GetEffectiveKey(e);
        if (key == Key.Enter)
        {
            _ = TryFindInTerminal((Keyboard.Modifiers & ModifierKeys.Shift) == 0);
            e.Handled = true;
            return;
        }

        if (key == Key.Escape)
        {
            CloseFindPanel();
            e.Handled = true;
        }
    }

    private void FindTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateFindMatchCount();
    }

    private void FindOptions_Changed(object sender, RoutedEventArgs e)
    {
        UpdateFindMatchCount();
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
        if (_session is not null || _isSessionTransitionActive || _isRecovering || _isClosingWindow)
        {
            return;
        }

        if (GetEffectiveKey(e) != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        StartButton_Click(StartButton, new RoutedEventArgs(Button.ClickEvent));
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        ModifierKeys modifiers = Keyboard.Modifiers;
        Key key = GetEffectiveKey(e);

        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.F)
        {
            OpenFindPanel();
            e.Handled = true;
            return;
        }

        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.S)
        {
            SaveTranscript();
            e.Handled = true;
            return;
        }

        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.R)
        {
            RestartButton_Click(RestartButton, new RoutedEventArgs(Button.ClickEvent));
            e.Handled = true;
            return;
        }

        if (modifiers == ModifierKeys.Control && key is Key.Add or Key.OemPlus)
        {
            ApplyTerminalFontSize(TerminalOutput.FontSize + 1);
            e.Handled = true;
            return;
        }

        if (modifiers == ModifierKeys.Control && key is Key.Subtract or Key.OemMinus)
        {
            ApplyTerminalFontSize(TerminalOutput.FontSize - 1);
            e.Handled = true;
            return;
        }

        if (modifiers == ModifierKeys.Control && key is Key.D0 or Key.NumPad0)
        {
            ApplyTerminalFontSize(DefaultTerminalFontSize);
            e.Handled = true;
            return;
        }

        if (FindPanel.Visibility == Visibility.Visible && key == Key.F3)
        {
            _ = TryFindInTerminal((modifiers & ModifierKeys.Shift) == 0);
            e.Handled = true;
            return;
        }

        if (FindPanel.Visibility == Visibility.Visible && key == Key.Escape)
        {
            CloseFindPanel();
            e.Handled = true;
        }
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
    /// HistoryPopup* dynamic resources. The accent (prompt / pointer / match
    /// highlight / caret) and selected-row colour follow the theme's selection
    /// colour, so a single theme change restyles both the terminal and the popup.
    /// </summary>
    private void ApplyHistoryPopupTheme(TerminalColorTheme theme)
    {
        Color selection = theme.SelectionBackground;
        Color accent = Color.FromRgb(selection.R, selection.G, selection.B);

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
        if (FindPanel.Visibility == Visibility.Visible)
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
        FindPanel.Visibility = Visibility.Visible;
        UpdateFindMatchCount();
        FindTextBox.Focus();
        FindTextBox.SelectAll();
    }

    private void CloseFindPanel()
    {
        FindPanel.Visibility = Visibility.Collapsed;
        FindCountText.Text = "Find";
        if (_session is not null)
        {
            FocusTerminalInput();
        }
    }

    private bool TryFindInTerminal(bool forward)
    {
        string query = FindTextBox.Text;
        if (string.IsNullOrWhiteSpace(query))
        {
            FindCountText.Text = "Type to search";
            return false;
        }

        StringComparison comparison = FindCaseSensitiveCheckBox.IsChecked == true
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        if (!TerminalOutput.TrySelectNextMatch(query, comparison, forward, out bool wrapped))
        {
            FindCountText.Text = "No match";
            return false;
        }

        FindCountText.Text = wrapped ? "Wrapped" : "Match";
        return true;
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

    private void UpdateFindMatchCount()
    {
        if (FindPanel.Visibility != Visibility.Visible)
        {
            return;
        }

        string query = FindTextBox.Text;
        if (string.IsNullOrWhiteSpace(query))
        {
            FindCountText.Text = "Type to search";
            return;
        }

        StringComparison comparison = FindCaseSensitiveCheckBox.IsChecked == true
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        int matchCount = TerminalOutput.CountMatches(query, comparison);
        FindCountText.Text = matchCount == 1 ? "1 match" : $"{matchCount} matches";
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
        if (_historySeeded)
        {
            return;
        }

        _historySeeded = true;
        if (!PSReadLineHistorySeedingEnabled)
        {
            return;
        }

        string? path = ResolvePSReadLineHistoryPath();
        if (path is null)
        {
            return;
        }

        IReadOnlyList<string> past = PSReadLineHistory.Read(path);
        if (past.Count > 0)
        {
            MergeSeedHistory(past);
        }
    }

    private string? ResolvePSReadLineHistoryPath()
    {
        // The shell reports its exact HistorySavePath via OSC 633;P when shell
        // integration is active; that is authoritative (handles custom paths).
        if (!string.IsNullOrWhiteSpace(_shellHistoryPath) && File.Exists(_shellHistoryPath))
        {
            return _shellHistoryPath;
        }

        // Otherwise only guess for PowerShell shells, probing the known defaults.
        string commandLine = string.IsNullOrWhiteSpace(_activeCommandLine) ? _initialCommandLine : _activeCommandLine;
        string executable = ExtractExecutableName(commandLine);
        bool isPowerShell = executable.Equals("pwsh", StringComparison.OrdinalIgnoreCase)
            || executable.Equals("powershell", StringComparison.OrdinalIgnoreCase);

        return isPowerShell ? PSReadLineHistory.FindDefaultHistoryPath() : null;
    }

    private static string ExtractExecutableName(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return string.Empty;
        }

        string trimmed = commandLine.TrimStart();
        string token;
        if (trimmed.StartsWith('"'))
        {
            int end = trimmed.IndexOf('"', 1);
            token = end > 0 ? trimmed[1..end] : trimmed[1..];
        }
        else
        {
            int space = trimmed.IndexOf(' ');
            token = space > 0 ? trimmed[..space] : trimmed;
        }

        return Path.GetFileNameWithoutExtension(token);
    }

    /// <summary>
    /// Merges <paramref name="olderFirst"/> (oldest first) ahead of the commands
    /// already recorded this session, deduplicating so the most recent
    /// occurrence keeps its position, then trims to the history cap.
    /// </summary>
    private void MergeSeedHistory(IReadOnlyList<string> olderFirst)
    {
        var combined = new List<string>(olderFirst.Count + _commandHistory.Count);
        combined.AddRange(olderFirst);
        combined.AddRange(_commandHistory);

        // Walk newest-to-oldest keeping the first time each command is seen, so
        // the most recent occurrence wins; reverse back to oldest-first order.
        var seen = new HashSet<string>();
        _commandHistory.Clear();
        for (int i = combined.Count - 1; i >= 0; i--)
        {
            string command = combined[i];
            if (string.IsNullOrWhiteSpace(command) || !seen.Add(command))
            {
                continue;
            }

            _commandHistory.Add(command);
        }

        _commandHistory.Reverse();

        if (_commandHistory.Count > CommandHistoryLimit)
        {
            _commandHistory.RemoveRange(0, _commandHistory.Count - CommandHistoryLimit);
        }
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
        string query = HistorySearchBox.Text;
        bool showAll = string.IsNullOrWhiteSpace(query);

        // recency = index into _commandHistory (higher = more recent), used to
        // break score ties and to order the unfiltered list newest-first.
        var ranked = new List<(int score, int recency, string command, string display, IReadOnlyList<int> matches)>();
        for (int i = 0; i < _commandHistory.Count; i++)
        {
            string command = _commandHistory[i];
            string display = command.ReplaceLineEndings("⏎");
            if (showAll)
            {
                ranked.Add((0, i, command, display, []));
            }
            else if (TryFuzzyMatch(display, query, out int score, out IReadOnlyList<int> matches))
            {
                ranked.Add((score, i, command, display, matches));
            }
        }

        ranked.Sort(static (a, b) =>
        {
            int byScore = b.score.CompareTo(a.score);
            return byScore != 0 ? byScore : b.recency.CompareTo(a.recency);
        });

        // fzf renders the best match at the bottom (next to the prompt), so add
        // the ranked list in reverse: worst on top, best last.
        HistoryResults.Items.Clear();
        for (int i = ranked.Count - 1; i >= 0; i--)
        {
            (int _, int _, string command, string display, IReadOnlyList<int> matches) = ranked[i];
            HistoryResults.Items.Add(BuildHistoryItem(command, display, matches));
        }

        if (HistoryResults.Items.Count > 0)
        {
            HistoryResults.SelectedIndex = HistoryResults.Items.Count - 1;
            HistoryResults.ScrollIntoView(HistoryResults.SelectedItem);
        }

        HistoryCountText.Text = $"{ranked.Count}/{_commandHistory.Count}";
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
        var matched = new HashSet<int>(matchedIndices);
        var segment = new StringBuilder();
        bool segmentHighlighted = false;

        void Flush()
        {
            if (segment.Length == 0)
            {
                return;
            }

            var run = new Run(segment.ToString());
            if (segmentHighlighted)
            {
                run.Foreground = highlight;
                run.FontWeight = FontWeights.Bold;
            }

            block.Inlines.Add(run);
            segment.Clear();
        }

        for (int i = 0; i < display.Length; i++)
        {
            bool isMatch = matched.Contains(i);
            if (isMatch != segmentHighlighted)
            {
                Flush();
                segmentHighlighted = isMatch;
            }

            segment.Append(display[i]);
        }

        Flush();
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
        score = 0;
        var indices = new List<int>(query.Length);
        matchedIndices = indices;

        int textIndex = 0;
        int previousMatch = -2;
        int consecutive = 0;

        foreach (char rawQueryChar in query)
        {
            if (char.IsWhiteSpace(rawQueryChar))
            {
                continue;
            }

            char queryChar = char.ToLowerInvariant(rawQueryChar);
            bool found = false;
            for (; textIndex < text.Length; textIndex++)
            {
                if (char.ToLowerInvariant(text[textIndex]) != queryChar)
                {
                    continue;
                }

                int charScore = 1;
                if (textIndex == previousMatch + 1)
                {
                    consecutive++;
                    charScore += 5 + consecutive;
                }
                else
                {
                    consecutive = 0;
                }

                if (textIndex == 0)
                {
                    charScore += 8;
                }
                else if (IsWordBoundary(text[textIndex - 1]))
                {
                    charScore += 6;
                }

                indices.Add(textIndex);
                score += charScore;
                previousMatch = textIndex;
                textIndex++;
                found = true;
                break;
            }

            if (!found)
            {
                score = 0;
                return false;
            }
        }

        return indices.Count > 0;
    }

    private static bool IsWordBoundary(char c)
        => c is ' ' or '/' or '\\' or '-' or '_' or '.' or ':' or '\t';

    private void HistorySearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (HistoryPopup.IsOpen)
        {
            UpdateHistoryResults();
        }
    }

    private void HistorySearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        switch (e.Key)
        {
            case Key.Escape:
                CloseHistoryPanel();
                e.Handled = true;
                break;
            case Key.Enter:
                AcceptHistorySelection();
                e.Handled = true;
                break;
            case Key.Down:
                MoveHistorySelection(1);
                e.Handled = true;
                break;
            case Key.Up:
                MoveHistorySelection(-1);
                e.Handled = true;
                break;
            case Key.N when ctrl:
                MoveHistorySelection(1);
                e.Handled = true;
                break;
            case Key.P when ctrl:
                MoveHistorySelection(-1);
                e.Handled = true;
                break;
            case Key.R when ctrl:
                // Repeating Ctrl+R walks upward to older matches, like reverse-i-search.
                MoveHistorySelection(-1);
                e.Handled = true;
                break;
        }
    }

    private void MoveHistorySelection(int delta)
    {
        int count = HistoryResults.Items.Count;
        if (count == 0)
        {
            return;
        }

        int next = Math.Clamp(HistoryResults.SelectedIndex + delta, 0, count - 1);
        HistoryResults.SelectedIndex = next;
        HistoryResults.ScrollIntoView(HistoryResults.SelectedItem);
    }

    private void HistoryResults_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        AcceptHistorySelection();
    }

    private void AcceptHistorySelection()
    {
        if (HistoryResults.SelectedItem is not FrameworkElement element || element.Tag is not string command)
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
            FileName = BuildTranscriptFileName()
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        File.WriteAllText(dialog.FileName, transcript, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        SetStatus($"Saved transcript: {dialog.FileName}");
    }

    private string BuildTranscriptFileName()
    {
        string basis = _terminalBuffer.WindowTitle;
        if (string.IsNullOrWhiteSpace(basis))
        {
            basis = string.IsNullOrWhiteSpace(_activeCommandLine)
                ? GetSelectedProfile().DisplayName
                : _activeCommandLine;
        }

        return $"{DateTime.Now:yyyyMMdd-HHmmss}-{SanitizeFileName(basis)}.txt";
    }

    private static string SanitizeFileName(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (char ch in name)
        {
            builder.Append(Path.GetInvalidFileNameChars().Contains(ch) ? '-' : ch);
        }

        string sanitized = builder.ToString().Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "terminal" : sanitized;
    }
}
