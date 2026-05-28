using System.ComponentModel;
using System.IO;

namespace Terminal.Sessions;

public static class ConPtyStartupDiagnostics
{
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorElevationRequired = 740;

    public static string BuildDiagnosticHint(Exception exception, string commandLine)
    {
        if (exception is not Win32Exception win32)
        {
            return string.Empty;
        }

        return win32.NativeErrorCode switch
        {
            ErrorFileNotFound => BuildFileNotFoundHint(commandLine),
            ErrorPathNotFound => "The working directory or executable path does not exist.",
            ErrorAccessDenied => "Access denied. The process may require elevated permissions.",
            ErrorInvalidParameter => "Invalid parameter. Check the command line syntax.",
            ErrorElevationRequired => "Elevation required. Try running as administrator.",
            _ => string.Empty
        };
    }

    internal static string ExtractExecutableName(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return string.Empty;
        }

        string trimmed = commandLine.Trim();

        if (trimmed.StartsWith('"'))
        {
            int closeQuote = trimmed.IndexOf('"', 1);
            if (closeQuote > 1)
                return Path.GetFileName(trimmed[1..closeQuote]);
            // Malformed unclosed quote: treat everything after the leading quote as the path.
            return Path.GetFileName(trimmed[1..]);
        }

        int spaceIndex = trimmed.IndexOf(' ');
        string token = spaceIndex < 0 ? trimmed : trimmed[..spaceIndex];
        return Path.GetFileName(token);
    }

    private static string BuildFileNotFoundHint(string commandLine)
    {
        string exe = ExtractExecutableName(commandLine);
        return string.IsNullOrEmpty(exe)
            ? "The executable was not found. Check the command line."
            : $"'{exe}' was not found. Check the path or verify it is on PATH.";
    }
}
