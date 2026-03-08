using Microsoft.Win32;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
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
            FontSize = TerminalOutput.FontSize
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
        _ = Dispatcher.BeginInvoke(UpdateTerminalViewportSize, DispatcherPriority.Loaded);
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
        _ = Dispatcher.BeginInvoke(UpdateTerminalViewportSize, DispatcherPriority.Loaded);
    }

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
        UpdateWindowTitle();
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
