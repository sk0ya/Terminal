using System.IO;
using System.Text;

namespace Terminal.Sessions;

/// <summary>
/// Reads the persistent command history PSReadLine keeps on disk, so the
/// terminal's Ctrl+R search can surface commands from previous sessions rather
/// than only those typed in the current one.
/// </summary>
public static class PSReadLineHistory
{
    /// <summary>
    /// Known default locations PSReadLine writes its console-host history to,
    /// most likely first. PSReadLine (both Windows PowerShell and PowerShell 7)
    /// defaults to the <c>Microsoft\Windows\PowerShell</c> path; the
    /// <c>Microsoft\PowerShell</c> variant covers alternative configurations.
    /// The authoritative path is reported by the shell at runtime
    /// (<c>(Get-PSReadLineOption).HistorySavePath</c>); these are a fallback for
    /// sessions without shell integration.
    /// </summary>
    public static IReadOnlyList<string> DefaultHistoryPaths
    {
        get
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return
            [
                Path.Combine(appData, "Microsoft", "Windows", "PowerShell", "PSReadLine", "ConsoleHost_history.txt"),
                Path.Combine(appData, "Microsoft", "PowerShell", "PSReadLine", "ConsoleHost_history.txt"),
            ];
        }
    }

    /// <summary>
    /// Returns the first <see cref="DefaultHistoryPaths"/> entry that exists on
    /// disk, or null when none do.
    /// </summary>
    public static string? FindDefaultHistoryPath()
    {
        foreach (string candidate in DefaultHistoryPaths)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Reads and parses the history file at <paramref name="path"/>, returning
    /// commands oldest-first. Returns an empty list when the file is missing or
    /// cannot be read.
    /// </summary>
    public static IReadOnlyList<string> Read(string path)
    {
        try
        {
            return File.Exists(path) ? Parse(File.ReadLines(path)) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Reconstructs whole commands from PSReadLine's line-based format. A
    /// physical line ending in an odd number of backticks is a continuation:
    /// the trailing backtick is dropped and the command resumes on the next line.
    /// </summary>
    public static IReadOnlyList<string> Parse(IEnumerable<string> lines)
    {
        var commands = new List<string>();
        var current = new StringBuilder();
        bool continuing = false;

        foreach (string line in lines)
        {
            if (EndsWithContinuation(line))
            {
                current.Append(line, 0, line.Length - 1).Append('\n');
                continuing = true;
                continue;
            }

            current.Append(line);
            commands.Add(current.ToString());
            current.Clear();
            continuing = false;
        }

        // A file that ends mid-continuation still yields the partial command.
        if (continuing && current.Length > 0)
        {
            commands.Add(current.ToString());
        }

        return commands;
    }

    private static bool EndsWithContinuation(string line)
    {
        int backticks = 0;
        for (int i = line.Length - 1; i >= 0 && line[i] == '`'; i--)
        {
            backticks++;
        }

        return backticks % 2 == 1;
    }
}
