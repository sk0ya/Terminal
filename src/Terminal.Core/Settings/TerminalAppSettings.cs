using System.Text.Json;
using System.IO;

namespace Terminal.Settings;

public sealed class TerminalAppSettings
{
    public const int DefaultScrollbackLimit = 10000;
    public const int MinScrollbackLimit = 100;
    public const int MaxScrollbackLimit = 1_000_000;
    public const double DefaultVerticalTabWidth = 190;
    public const double MinVerticalTabWidth = 120;
    public const double MaxVerticalTabWidth = 420;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    public string SelectedProfileId { get; set; } = "cmd";
    public string CommandLine { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public string FontFamilyName { get; set; } = "Cascadia Mono";
    public double FontSize { get; set; } = 14;
    public string TabStripPlacement { get; set; } = TerminalTabStripPlacementCatalog.Top;
    public double WindowWidth { get; set; } = 1000;
    public double WindowHeight { get; set; } = 720;
    public bool EnableSessionLogging { get; set; } = true;
    public bool EnableShellIntegrationInjection { get; set; } = true;
    public bool ShowStatusBar { get; set; } = false;
    public string? SessionLogDirectory { get; set; }
    public bool CjkAmbiguousWidthIsWide { get; set; } = false;
    public string BackdropType { get; set; } = "none";
    public bool EnableFontLigatures { get; set; } = false;
    public double VerticalTabWidth { get; set; } = DefaultVerticalTabWidth;
    public bool VerticalTabsCollapsed { get; set; } = false;
    public Dictionary<string, string> KeyBindings { get; set; } = TerminalKeyBindingCatalog.CreateDefaults();
    public string ColorScheme { get; set; } = "Dark";
    public string? CustomForeground { get; set; }
    public string? CustomBackground { get; set; }
    public string? CustomCursorColor { get; set; }
    public string? CustomSelectionColor { get; set; }
    public string[]? CustomAnsiPalette { get; set; }
    public List<TerminalSavedTab> SavedTabs { get; set; } = [];
    public int ActiveTabIndex { get; set; }
    public bool ConfirmCloseWithRunningProcesses { get; set; } = true;

    /// <summary>
    /// Maximum number of scrollback lines a terminal buffer keeps. Applied to
    /// buffers created after the change (new tabs / restarted sessions).
    /// </summary>
    public int ScrollbackLimit { get; set; } = DefaultScrollbackLimit;

    /// <summary>Clamps a scrollback limit to the supported range.</summary>
    public static int ClampScrollbackLimit(int value)
        => Math.Clamp(value, MinScrollbackLimit, MaxScrollbackLimit);

    public static double ClampVerticalTabWidth(double value)
        => double.IsFinite(value)
            ? Math.Clamp(value, MinVerticalTabWidth, MaxVerticalTabWidth)
            : DefaultVerticalTabWidth;

    public static TerminalAppSettings Load()
    {
        string path = GetSettingsPath();
        if (!File.Exists(path))
        {
            return new TerminalAppSettings();
        }

        try
        {
            string json = File.ReadAllText(path);
            TerminalAppSettings settings = JsonSerializer.Deserialize<TerminalAppSettings>(json, SerializerOptions) ?? new TerminalAppSettings();
            if (string.IsNullOrWhiteSpace(settings.FontFamilyName))
            {
                settings.FontFamilyName = "Cascadia Mono";
            }

            settings.TabStripPlacement = TerminalTabStripPlacementCatalog.Normalize(settings.TabStripPlacement);
            settings.ScrollbackLimit = ClampScrollbackLimit(settings.ScrollbackLimit);
            settings.VerticalTabWidth = ClampVerticalTabWidth(settings.VerticalTabWidth);
            settings.KeyBindings = TerminalKeyBindingCatalog.Normalize(settings.KeyBindings);
            settings.SavedTabs ??= [];

            return settings;
        }
        catch
        {
            return new TerminalAppSettings();
        }
    }

    public void Save()
    {
        string path = GetSettingsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string json = JsonSerializer.Serialize(this, SerializerOptions);
        File.WriteAllText(path, json);
    }

    private static string GetSettingsPath()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "Terminal", "settings.json");
    }
}

public sealed record TerminalSavedTab(string CommandLine, string WorkingDirectory);
