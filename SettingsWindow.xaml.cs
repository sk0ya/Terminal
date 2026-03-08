using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ConPtyTerminal;

public partial class SettingsWindow : Window
{
    private static readonly TimeSpan AutoApplyDelay = TimeSpan.FromMilliseconds(300);
    private readonly List<TerminalProfileDefinition> _profiles = [];
    private readonly List<string> _fontFamilyNames = [];
    private readonly TerminalProfileDefinition _customProfile = new(
        "custom",
        "Custom",
        string.Empty,
        "Use any executable or shell command line.",
        IsCustom: true);
    private readonly TerminalAppSettings _currentSettings;
    private readonly DispatcherTimer _workingDirectoryApplyTimer = new();
    private readonly DispatcherTimer _fontSizeApplyTimer = new();
    private bool _suppressProfileSelectionChanged;
    private bool _suppressCommandTextChanged;
    private bool _suppressAutoApply;

    public SettingsWindow(TerminalAppSettings settings)
    {
        _currentSettings = CloneSettings(settings);
        InitializeComponent();
        _workingDirectoryApplyTimer.Interval = AutoApplyDelay;
        _workingDirectoryApplyTimer.Tick += WorkingDirectoryApplyTimer_Tick;
        _fontSizeApplyTimer.Interval = AutoApplyDelay;
        _fontSizeApplyTimer.Tick += FontSizeApplyTimer_Tick;
        BuildProfileCatalog();
        BuildFontFamilyCatalog();
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
            SetSelectedProfile(settings.SelectedProfileId, commandLine);
            SetInputValidationState(WorkingDirectoryTextBox, isValid: true);
            SetInputValidationState(FontSizeTextBox, isValid: true);
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

        SetInputValidationState(WorkingDirectoryTextBox, TryNormalizeWorkingDirectory(WorkingDirectoryTextBox.Text, out _));
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

        SetInputValidationState(FontSizeTextBox, TryNormalizeFontSize(FontSizeTextBox.Text, out _));
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

    private void FontSizeTextBox_LostKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        CommitFontSizeSetting();
    }

    private void WorkingDirectoryApplyTimer_Tick(object? sender, EventArgs e)
    {
        CommitWorkingDirectorySetting();
    }

    private void FontSizeApplyTimer_Tick(object? sender, EventArgs e)
    {
        CommitFontSizeSetting();
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

        if (!TryNormalizeWorkingDirectory(WorkingDirectoryTextBox.Text, out string workingDirectory))
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

        if (!TryNormalizeFontSize(FontSizeTextBox.Text, out double fontSize))
        {
            SetInputValidationState(FontSizeTextBox, isValid: false);
            return;
        }

        _currentSettings.FontSize = fontSize;
        SetInputValidationState(FontSizeTextBox, isValid: true);
        SetTextSilently(FontSizeTextBox, fontSize.ToString("0"));
        PublishSettingsChanged();
    }

    private void PublishSettingsChanged()
    {
        SettingsChanged?.Invoke(CloneSettings(_currentSettings));
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

    private static bool TryNormalizeWorkingDirectory(string? rawPath, out string workingDirectory)
    {
        try
        {
            workingDirectory = NormalizeWorkingDirectory(rawPath);
            return true;
        }
        catch
        {
            workingDirectory = string.Empty;
            return false;
        }
    }

    private static bool TryNormalizeFontSize(string? rawValue, out double fontSize)
    {
        if (!double.TryParse(rawValue?.Trim(), out double parsedValue))
        {
            fontSize = 0;
            return false;
        }

        fontSize = Math.Round(Math.Clamp(parsedValue, 11, 24));
        return true;
    }

    private static TerminalAppSettings CloneSettings(TerminalAppSettings settings)
    {
        return new TerminalAppSettings
        {
            SelectedProfileId = settings.SelectedProfileId,
            CommandLine = settings.CommandLine,
            WorkingDirectory = settings.WorkingDirectory,
            FontFamilyName = settings.FontFamilyName,
            FontSize = settings.FontSize,
            WindowWidth = settings.WindowWidth,
            WindowHeight = settings.WindowHeight
        };
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
}
