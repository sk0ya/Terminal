using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace ConPtyTerminal;

public partial class SettingsWindow : Window
{
    private readonly List<TerminalProfileDefinition> _profiles = [];
    private readonly TerminalProfileDefinition _customProfile = new(
        "custom",
        "Custom",
        string.Empty,
        "Use any executable or shell command line.",
        IsCustom: true);
    private readonly TerminalAppSettings _seed;
    private bool _suppressProfileSelectionChanged;
    private bool _suppressCommandTextChanged;

    public SettingsWindow(TerminalAppSettings settings)
    {
        _seed = settings;
        InitializeComponent();
        BuildProfileCatalog();
        ApplySettings(settings);
    }

    public TerminalAppSettings SavedSettings { get; private set; } = new();

    private void BuildProfileCatalog()
    {
        _profiles.Clear();
        _profiles.AddRange(TerminalProfileCatalog.CreateProfiles());
        _profiles.Add(_customProfile);
        ProfileComboBox.ItemsSource = _profiles;
        ProfileComboBox.DisplayMemberPath = nameof(TerminalProfileDefinition.DisplayName);
    }

    private void ApplySettings(TerminalAppSettings settings)
    {
        string commandLine = string.IsNullOrWhiteSpace(settings.CommandLine)
            ? TerminalProfileCatalog.BuildDefaultCommandLine()
            : settings.CommandLine.Trim();
        string workingDirectory = string.IsNullOrWhiteSpace(settings.WorkingDirectory)
            ? Environment.CurrentDirectory
            : settings.WorkingDirectory.Trim();

        WorkingDirectoryTextBox.Text = workingDirectory;
        FontSizeTextBox.Text = settings.FontSize.ToString("0");
        SetSelectedProfile(settings.SelectedProfileId, commandLine);
    }

    private void SetSelectedProfile(string? profileId, string commandLine)
    {
        TerminalProfileDefinition selectedProfile =
            _profiles.FirstOrDefault(profile => string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase)) ??
            MatchProfileByCommandLine(commandLine) ??
            _customProfile;

        string effectiveCommandLine = string.IsNullOrWhiteSpace(commandLine) && !selectedProfile.IsCustom
            ? selectedProfile.CommandLine
            : commandLine;

        _suppressProfileSelectionChanged = true;
        ProfileComboBox.SelectedItem = selectedProfile;
        _suppressProfileSelectionChanged = false;

        _suppressCommandTextChanged = true;
        CommandTextBox.Text = effectiveCommandLine;
        _suppressCommandTextChanged = false;

        UpdateDescription();
    }

    private TerminalProfileDefinition? MatchProfileByCommandLine(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        string normalized = commandLine.Trim();
        return _profiles.FirstOrDefault(profile =>
            !profile.IsCustom &&
            string.Equals(profile.CommandLine, normalized, StringComparison.OrdinalIgnoreCase));
    }

    private TerminalProfileDefinition GetSelectedProfile()
    {
        return ProfileComboBox.SelectedItem as TerminalProfileDefinition ?? _customProfile;
    }

    private void UpdateDescription()
    {
        DescriptionText.Text = GetSelectedProfile().Description;
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

        UpdateDescription();
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
        UpdateDescription();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string workingDirectory = NormalizeWorkingDirectory(WorkingDirectoryTextBox.Text);
            if (!double.TryParse(FontSizeTextBox.Text.Trim(), out double fontSize))
            {
                throw new InvalidOperationException("Font size must be a number.");
            }

            fontSize = Math.Round(Math.Clamp(fontSize, 11, 24));
            SavedSettings = new TerminalAppSettings
            {
                SelectedProfileId = GetSelectedProfile().Id,
                CommandLine = string.IsNullOrWhiteSpace(CommandTextBox.Text)
                    ? TerminalProfileCatalog.BuildDefaultCommandLine()
                    : CommandTextBox.Text.Trim(),
                WorkingDirectory = workingDirectory,
                FontSize = fontSize,
                WindowWidth = _seed.WindowWidth,
                WindowHeight = _seed.WindowHeight
            };

            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Invalid settings", MessageBoxButton.OK, MessageBoxImage.Warning);
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
}
