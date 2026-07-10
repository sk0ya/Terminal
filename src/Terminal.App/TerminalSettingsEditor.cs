using System.IO;
using Terminal.Settings;

namespace Terminal;

internal static class TerminalSettingsEditor
{
    internal static bool TryNormalizeWorkingDirectory(string? rawPath, out string workingDirectory)
    {
        try
        {
            string candidate = string.IsNullOrWhiteSpace(rawPath)
                ? Environment.CurrentDirectory
                : Environment.ExpandEnvironmentVariables(rawPath.Trim());
            workingDirectory = Path.GetFullPath(candidate);
            return Directory.Exists(workingDirectory);
        }
        catch
        {
            workingDirectory = string.Empty;
            return false;
        }
    }

    internal static bool TryNormalizeFontSize(string? rawValue, out double fontSize)
    {
        if (!double.TryParse(rawValue?.Trim(), out double parsed)) { fontSize = 0; return false; }
        fontSize = Math.Round(Math.Clamp(parsed, 11, 24));
        return true;
    }

    internal static bool TryNormalizeScrollbackLimit(string? rawValue, out int scrollbackLimit)
    {
        if (!int.TryParse(rawValue?.Trim(), out int parsed)) { scrollbackLimit = 0; return false; }
        scrollbackLimit = TerminalAppSettings.ClampScrollbackLimit(parsed);
        return true;
    }

    internal static TerminalAppSettings Clone(TerminalAppSettings settings) => new()
    {
        SelectedProfileId = settings.SelectedProfileId,
        CommandLine = settings.CommandLine,
        WorkingDirectory = settings.WorkingDirectory,
        FontFamilyName = settings.FontFamilyName,
        FontSize = settings.FontSize,
        TabStripPlacement = TerminalTabStripPlacementCatalog.Normalize(settings.TabStripPlacement),
        WindowWidth = settings.WindowWidth,
        WindowHeight = settings.WindowHeight,
        EnableSessionLogging = settings.EnableSessionLogging,
        EnableShellIntegrationInjection = settings.EnableShellIntegrationInjection,
        ShowStatusBar = settings.ShowStatusBar,
        SessionLogDirectory = settings.SessionLogDirectory,
        CjkAmbiguousWidthIsWide = settings.CjkAmbiguousWidthIsWide,
        BackdropType = settings.BackdropType,
        EnableFontLigatures = settings.EnableFontLigatures,
        ScrollbackLimit = settings.ScrollbackLimit
    };
}
