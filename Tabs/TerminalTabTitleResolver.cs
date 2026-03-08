using System.IO;

using Terminal.Sessions;
using Terminal.Settings;

namespace Terminal.Tabs;

internal static class TerminalTabTitleResolver
{
    private static readonly string[] TitleSeparators = [" - ", " | ", " — ", " – "];

    public static string Resolve(string? terminalTitle, string? commandLine, TerminalProfileDefinition profile)
    {
        string fallbackTitle = ResolveProfileOrAppTitle(commandLine, profile);
        string? dynamicTitle = TryResolveDynamicTitle(terminalTitle, fallbackTitle);
        return dynamicTitle ?? fallbackTitle;
    }

    internal static string ResolveProfileOrAppTitle(string? commandLine, TerminalProfileDefinition profile)
    {
        if (!profile.IsCustom && !string.IsNullOrWhiteSpace(profile.DisplayName))
        {
            return profile.DisplayName.Trim();
        }

        string appTitle = BuildAppTitle(commandLine);
        return string.IsNullOrWhiteSpace(appTitle) ? "Terminal" : appTitle;
    }

    internal static string? TryResolveDynamicTitle(string? terminalTitle, string fallbackTitle)
    {
        string normalizedTerminalTitle = NormalizeTitle(terminalTitle);
        if (normalizedTerminalTitle.Length == 0)
        {
            return null;
        }

        string normalizedFallbackTitle = NormalizeTitle(fallbackTitle);
        foreach (string candidate in EnumerateTitleCandidates(normalizedTerminalTitle))
        {
            if (candidate.Length == 0 || LooksLikePath(candidate) || IsShellAdornment(candidate))
            {
                continue;
            }

            if (!string.Equals(candidate, normalizedFallbackTitle, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateTitleCandidates(string terminalTitle)
    {
        foreach (string separator in TitleSeparators)
        {
            if (!terminalTitle.Contains(separator, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (string segment in terminalTitle.Split(separator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                yield return NormalizeTitle(segment);
            }
        }

        yield return terminalTitle;
    }

    private static string NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        string normalized = title.Trim();
        if (normalized.StartsWith("Administrator: ", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["Administrator: ".Length..].Trim();
        }
        else if (normalized.StartsWith("管理者: ", StringComparison.Ordinal))
        {
            normalized = normalized["管理者: ".Length..].Trim();
        }

        return normalized;
    }

    private static bool LooksLikePath(string value)
    {
        if (value.IndexOfAny(['\\', '/']) >= 0)
        {
            return true;
        }

        return value.Length >= 2 &&
            char.IsLetter(value[0]) &&
            value[1] == ':';
    }

    private static bool IsShellAdornment(string value)
    {
        return value.Equals("Administrator", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Running as Administrator", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildAppTitle(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return "Terminal";
        }

        try
        {
            (string fileName, _) = ProcessPipeSession.SplitCommandLine(commandLine);
            string title = Path.GetFileNameWithoutExtension(fileName);
            return string.IsNullOrWhiteSpace(title) ? "Terminal" : title;
        }
        catch
        {
            return "Terminal";
        }
    }
}
