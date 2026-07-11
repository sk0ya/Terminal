using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

using Terminal.Settings;

namespace Terminal;

public partial class SettingsWindow : Window
{
    private static readonly TimeSpan AutoApplyDelay = TimeSpan.FromMilliseconds(300);
    private readonly List<TerminalProfileDefinition> _profiles = [];
    private readonly List<string> _fontFamilyNames = [];
    private readonly List<TerminalTabStripPlacementOption> _tabStripPlacements = [];
    private readonly TerminalProfileDefinition _customProfile = new(
        "custom",
        "Custom",
        string.Empty,
        "Use any executable or shell command line.",
        IsCustom: true);
    private readonly TerminalAppSettings _currentSettings;
    private readonly DispatcherTimer _workingDirectoryApplyTimer = new();
    private readonly DispatcherTimer _fontSizeApplyTimer = new();
    private readonly DispatcherTimer _scrollbackLimitApplyTimer = new();
    private bool _suppressProfileSelectionChanged;
    private bool _suppressCommandTextChanged;
    private bool _suppressAutoApply;

    public SettingsWindow(TerminalAppSettings settings)
    {
        _currentSettings = TerminalSettingsEditor.Clone(settings);
        InitializeComponent();
        _workingDirectoryApplyTimer.Interval = AutoApplyDelay;
        _workingDirectoryApplyTimer.Tick += WorkingDirectoryApplyTimer_Tick;
        _fontSizeApplyTimer.Interval = AutoApplyDelay;
        _fontSizeApplyTimer.Tick += FontSizeApplyTimer_Tick;
        _scrollbackLimitApplyTimer.Interval = AutoApplyDelay;
        _scrollbackLimitApplyTimer.Tick += ScrollbackLimitApplyTimer_Tick;
        BuildProfileCatalog();
        BuildFontFamilyCatalog();
        BuildTabStripPlacementCatalog();
        ApplySettings(_currentSettings);
    }

    public event Action<TerminalAppSettings>? SettingsChanged;

    private void BuildProfileCatalog()
    {
        _profiles.Clear();
        _profiles.AddRange(TerminalProfileCatalog.CreateProfiles());
        _profiles.Add(_customProfile);
        ProfileComboBox.ItemsSource = _profiles;
    }

    private void BuildFontFamilyCatalog()
    {
        _fontFamilyNames.Clear();
        _fontFamilyNames.AddRange(TerminalFontCatalog.CreateFontFamilyNames());
        FontFamilyComboBox.ItemsSource = _fontFamilyNames;
    }

    private void BuildTabStripPlacementCatalog()
    {
        _tabStripPlacements.Clear();
        _tabStripPlacements.AddRange(TerminalTabStripPlacementCatalog.CreateOptions());
        TabStripPlacementComboBox.ItemsSource = _tabStripPlacements;
    }

    private void ApplySettings(TerminalAppSettings settings)
    {
        _suppressAutoApply = true;
        try
        {
            string commandLine = string.IsNullOrWhiteSpace(settings.CommandLine)
                ? TerminalProfileCatalog.BuildDefaultCommandLine()
                : settings.CommandLine.Trim();
            string workingDirectory = string.IsNullOrWhiteSpace(settings.WorkingDirectory)
                ? Environment.CurrentDirectory
                : settings.WorkingDirectory.Trim();

            WorkingDirectoryTextBox.Text = workingDirectory;
            FontFamilyComboBox.SelectedItem = TerminalFontCatalog.NormalizeFontFamilyName(settings.FontFamilyName);
            FontSizeTextBox.Text = settings.FontSize.ToString("0");
            TabStripPlacementComboBox.SelectedItem = TerminalTabStripPlacementCatalog.ResolveSelectedOption(settings.TabStripPlacement);
            SetSelectedProfile(settings.SelectedProfileId, commandLine);
            StatusBarCheckBox.IsChecked = settings.ShowStatusBar;
            FontLigaturesCheckBox.IsChecked = settings.EnableFontLigatures;
            CloseConfirmationCheckBox.IsChecked = settings.ConfirmCloseWithRunningProcesses;
            ScrollbackLimitTextBox.Text = TerminalAppSettings.ClampScrollbackLimit(settings.ScrollbackLimit).ToString();
            SetInputValidationState(WorkingDirectoryTextBox, isValid: true);
            SetInputValidationState(FontSizeTextBox, isValid: true);
            SetInputValidationState(ScrollbackLimitTextBox, isValid: true);
        }
        finally
        {
            _suppressAutoApply = false;
        }
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
    }

    private TerminalProfileDefinition? MatchProfileByCommandLine(string? commandLine)
    {
        return TerminalProfileCatalog.MatchProfileByCommandLine(_profiles, commandLine);
    }

    private TerminalProfileDefinition GetSelectedProfile()
    {
        return ProfileComboBox.SelectedItem as TerminalProfileDefinition ?? _customProfile;
    }

    private void ProfileComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressProfileSelectionChanged || _suppressAutoApply)
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

        CommitCommandSettings(profile.Id);
    }

    private void CommandTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressCommandTextChanged || _suppressAutoApply)
        {
            return;
        }

        TerminalProfileDefinition matchedProfile = MatchProfileByCommandLine(CommandTextBox.Text) ?? _customProfile;
        _suppressProfileSelectionChanged = true;
        ProfileComboBox.SelectedItem = matchedProfile;
        _suppressProfileSelectionChanged = false;
        CommitCommandSettings(matchedProfile.Id);
    }

    private void WorkingDirectoryTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressAutoApply)
        {
            return;
        }

        SetInputValidationState(WorkingDirectoryTextBox, TerminalSettingsEditor.TryNormalizeWorkingDirectory(WorkingDirectoryTextBox.Text, out _));
        RestartTimer(_workingDirectoryApplyTimer);
    }

    private void WorkingDirectoryTextBox_LostKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        CommitWorkingDirectorySetting();
    }

    private void FontSizeTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressAutoApply)
        {
            return;
        }

        SetInputValidationState(FontSizeTextBox, TerminalSettingsEditor.TryNormalizeFontSize(FontSizeTextBox.Text, out _));
        RestartTimer(_fontSizeApplyTimer);
    }

    private void FontFamilyComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressAutoApply)
        {
            return;
        }

        CommitFontFamilySetting();
    }

    private void TabStripPlacementComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressAutoApply)
        {
            return;
        }

        CommitTabStripPlacementSetting();
    }

    private void FontSizeTextBox_LostKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        CommitFontSizeSetting();
    }

    private void ScrollbackLimitTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressAutoApply)
        {
            return;
        }

        SetInputValidationState(ScrollbackLimitTextBox, TerminalSettingsEditor.TryNormalizeScrollbackLimit(ScrollbackLimitTextBox.Text, out _));
        RestartTimer(_scrollbackLimitApplyTimer);
    }

    private void ScrollbackLimitTextBox_LostKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        CommitScrollbackLimitSetting();
    }

    private void WorkingDirectoryApplyTimer_Tick(object? sender, EventArgs e)
    {
        CommitWorkingDirectorySetting();
    }

    private void FontSizeApplyTimer_Tick(object? sender, EventArgs e)
    {
        CommitFontSizeSetting();
    }

    private void ScrollbackLimitApplyTimer_Tick(object? sender, EventArgs e)
    {
        CommitScrollbackLimitSetting();
    }

    private void CommitCommandSettings(string profileId)
    {
        _currentSettings.SelectedProfileId = profileId;
        _currentSettings.CommandLine = string.IsNullOrWhiteSpace(CommandTextBox.Text)
            ? TerminalProfileCatalog.BuildDefaultCommandLine()
            : CommandTextBox.Text.Trim();
        PublishSettingsChanged();
    }

    private void CommitWorkingDirectorySetting()
    {
        _workingDirectoryApplyTimer.Stop();
        if (_suppressAutoApply)
        {
            return;
        }

        if (!TerminalSettingsEditor.TryNormalizeWorkingDirectory(WorkingDirectoryTextBox.Text, out string workingDirectory))
        {
            SetInputValidationState(WorkingDirectoryTextBox, isValid: false);
            return;
        }

        _currentSettings.WorkingDirectory = workingDirectory;
        SetInputValidationState(WorkingDirectoryTextBox, isValid: true);
        SetTextSilently(WorkingDirectoryTextBox, workingDirectory);
        PublishSettingsChanged();
    }

    private void CommitFontFamilySetting()
    {
        if (_suppressAutoApply)
        {
            return;
        }

        string fontFamilyName = TerminalFontCatalog.NormalizeFontFamilyName(FontFamilyComboBox.SelectedItem as string);
        _currentSettings.FontFamilyName = fontFamilyName;
        SetComboSelectionSilently(FontFamilyComboBox, fontFamilyName);
        PublishSettingsChanged();
    }

    private void CommitFontSizeSetting()
    {
        _fontSizeApplyTimer.Stop();
        if (_suppressAutoApply)
        {
            return;
        }

        if (!TerminalSettingsEditor.TryNormalizeFontSize(FontSizeTextBox.Text, out double fontSize))
        {
            SetInputValidationState(FontSizeTextBox, isValid: false);
            return;
        }

        _currentSettings.FontSize = fontSize;
        SetInputValidationState(FontSizeTextBox, isValid: true);
        SetTextSilently(FontSizeTextBox, fontSize.ToString("0"));
        PublishSettingsChanged();
    }

    private void CommitScrollbackLimitSetting()
    {
        _scrollbackLimitApplyTimer.Stop();
        if (_suppressAutoApply)
        {
            return;
        }

        if (!TerminalSettingsEditor.TryNormalizeScrollbackLimit(ScrollbackLimitTextBox.Text, out int scrollbackLimit))
        {
            SetInputValidationState(ScrollbackLimitTextBox, isValid: false);
            return;
        }

        _currentSettings.ScrollbackLimit = scrollbackLimit;
        SetInputValidationState(ScrollbackLimitTextBox, isValid: true);
        SetTextSilently(ScrollbackLimitTextBox, scrollbackLimit.ToString());
        PublishSettingsChanged();
    }

    private void CommitTabStripPlacementSetting()
    {
        if (_suppressAutoApply)
        {
            return;
        }

        string placement = TerminalTabStripPlacementCatalog.Normalize(
            (TabStripPlacementComboBox.SelectedItem as TerminalTabStripPlacementOption)?.Id);
        _currentSettings.TabStripPlacement = placement;
        SetComboSelectionSilently(
            TabStripPlacementComboBox,
            TerminalTabStripPlacementCatalog.ResolveSelectedOption(placement));
        PublishSettingsChanged();
    }

    private void StatusBarCheckBox_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressAutoApply)
        {
            return;
        }

        _currentSettings.ShowStatusBar = StatusBarCheckBox.IsChecked == true;
        PublishSettingsChanged();
    }

    private void FontLigaturesCheckBox_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressAutoApply)
        {
            return;
        }

        _currentSettings.EnableFontLigatures = FontLigaturesCheckBox.IsChecked == true;
        PublishSettingsChanged();
    }

    private void PublishSettingsChanged()
    {
        // Editing is staged locally. The settings are published only from the
        // explicit Apply and Save actions.
    }

    private void RestartTimer(DispatcherTimer timer)
    {
        timer.Stop();
        timer.Start();
    }

    private void SetTextSilently(TextBox textBox, string value)
    {
        if (string.Equals(textBox.Text, value, StringComparison.Ordinal))
        {
            return;
        }

        _suppressAutoApply = true;
        try
        {
            textBox.Text = value;
        }
        finally
        {
            _suppressAutoApply = false;
        }
    }

    private void SetComboSelectionSilently(ComboBox comboBox, object value)
    {
        if (Equals(comboBox.SelectedItem, value))
        {
            return;
        }

        _suppressAutoApply = true;
        try
        {
            comboBox.SelectedItem = value;
        }
        finally
        {
            _suppressAutoApply = false;
        }
    }

    private void SetInputValidationState(Control control, bool isValid)
    {
        control.BorderBrush = (Brush)FindResource(isValid ? "BorderBrush" : "InvalidBrush");
    }

    private bool TryCommitAllInputs()
    {
        CommitWorkingDirectorySetting();
        CommitFontSizeSetting();
        CommitScrollbackLimitSetting();

        bool isValid = TerminalSettingsEditor.TryNormalizeWorkingDirectory(WorkingDirectoryTextBox.Text, out _)
            && TerminalSettingsEditor.TryNormalizeFontSize(FontSizeTextBox.Text, out _)
            && TerminalSettingsEditor.TryNormalizeScrollbackLimit(ScrollbackLimitTextBox.Text, out _);
        return isValid;
    }

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (TryCommitAllInputs())
        {
            SettingsChanged?.Invoke(TerminalSettingsEditor.Clone(_currentSettings));
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryCommitAllInputs())
        {
            return;
        }

        SettingsChanged?.Invoke(TerminalSettingsEditor.Clone(_currentSettings));
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void KeyBindingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new KeyBindingsWindow(_currentSettings.KeyBindings) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _currentSettings.KeyBindings = dialog.Bindings;
        }
    }

    private void ColorSchemeButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ColorSchemeWindow(_currentSettings) { Owner = this };
        if (dialog.ShowDialog() == true) dialog.ApplyTo(_currentSettings);
    }

    private void CloseConfirmationCheckBox_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_suppressAutoApply) _currentSettings.ConfirmCloseWithRunningProcesses = CloseConfirmationCheckBox.IsChecked == true;
    }

}
