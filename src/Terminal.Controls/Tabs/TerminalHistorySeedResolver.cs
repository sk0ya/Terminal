using System.IO;

namespace Terminal.Tabs;

internal static class TerminalHistorySeedResolver
{
    public static string? ResolvePath(
        string? reportedHistoryPath,
        string? commandLine,
        Func<string, bool> fileExists,
        Func<string?> findDefaultHistoryPath)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(findDefaultHistoryPath);

        if (!string.IsNullOrWhiteSpace(reportedHistoryPath) && fileExists(reportedHistoryPath))
        {
            return reportedHistoryPath;
        }

        string executable = ExtractExecutableName(commandLine);
        bool isPowerShell = executable.Equals("pwsh", StringComparison.OrdinalIgnoreCase)
            || executable.Equals("powershell", StringComparison.OrdinalIgnoreCase);

        return isPowerShell ? findDefaultHistoryPath() : null;
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
}
