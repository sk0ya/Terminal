using System.Text.Json;
using System.IO;

namespace ConPtyTerminal;

public sealed class TerminalAppSettings
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    public string SelectedProfileId { get; set; } = "cmd";
    public string CommandLine { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public string FontFamilyName { get; set; } = TerminalFontCatalog.DefaultFontFamilyName;
    public double FontSize { get; set; } = 14;
    public double WindowWidth { get; set; } = 1000;
    public double WindowHeight { get; set; } = 720;

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
                settings.FontFamilyName = TerminalFontCatalog.DefaultFontFamilyName;
            }

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
        return Path.Combine(localAppData, "ConPtyTerminal", "settings.json");
    }
}
