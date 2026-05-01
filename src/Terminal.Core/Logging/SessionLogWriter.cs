using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Terminal.Sessions;

namespace Terminal.Logging;

internal sealed partial class SessionLogWriter : ISessionLogger
{
    private readonly object _writeLock = new();
    private readonly string _sid;
    private StreamWriter? _writer;
    private DateTime _startedAtUtc;
    private bool _disposed;

    // CSI and OSC alternatives must come before the generic Fe escape to avoid early match on [ and ].
    [GeneratedRegex(@"\x1b(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\r\n]*(?:\x07|\x1b\\)?|[@-Z\\-_]|.)")]
    private static partial Regex AnsiPattern();

    // CUF (cursor forward \x1b[nC) and CHA (cursor to column \x1b[nG) — replace with space before full ANSI strip.
    [GeneratedRegex(@"\x1b\[[\d;]*[CG]")]
    private static partial Regex CursorRightPattern();

    // Box Drawing (U+2500–U+257F), Block Elements (U+2580–U+259F), Misc Technical (U+2300–U+23FF).
    [GeneratedRegex(@"[⌀-⏿─-▟]")]
    private static partial Regex UiCharsPattern();

    [GeneratedRegex(@" {2,}")]
    private static partial Regex MultiSpacePattern();

    // Progress/status lines to skip: ellipsis-terminated progress, Claude Code status bar, bare prompt.
    [GeneratedRegex(@"…$|·/effort|\(shift\+tab to cycle\)|esc to interrupt|^\s*>\s*$")]
    private static partial Regex ProgressLinePattern();

    private SessionLogWriter(StreamWriter writer, string sid)
    {
        _writer = writer;
        _sid = sid;
    }

    public static SessionLogWriter Create(string commandLine, string workingDirectory, string? logDirectoryOverride)
    {
        string project = ResolveProjectName(workingDirectory);
        string baseDir = ResolveBaseLogDirectory(logDirectoryOverride);
        string projectDir = Path.Combine(baseDir, project);
        Directory.CreateDirectory(projectDir);

        string today = DateTime.Now.ToString("yyyy-MM-dd");
        string sid = Guid.NewGuid().ToString("N")[..8];
        string filePath = Path.Combine(projectDir, $"{today}.jsonl");
        var fileStream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        var writer = new StreamWriter(fileStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            NewLine = "\n",
            AutoFlush = false
        };
        return new SessionLogWriter(writer, sid);
    }

    public static void CompressOldDayFiles(string? logDirectoryOverride = null)
    {
        try
        {
            string baseDir = ResolveBaseLogDirectory(logDirectoryOverride);
            if (!Directory.Exists(baseDir))
            {
                return;
            }

            string today = DateTime.Now.ToString("yyyy-MM-dd");
            foreach (string projectDir in Directory.GetDirectories(baseDir))
            {
                foreach (string jsonlFile in Directory.GetFiles(projectDir, "????-??-??.jsonl"))
                {
                    string dayName = Path.GetFileNameWithoutExtension(jsonlFile);
                    if (string.Compare(dayName, today, StringComparison.Ordinal) >= 0)
                    {
                        continue;
                    }

                    string zipPath = Path.ChangeExtension(jsonlFile, ".zip");
                    if (File.Exists(zipPath))
                    {
                        continue;
                    }

                    using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                    {
                        zip.CreateEntryFromFile(jsonlFile, Path.GetFileName(jsonlFile));
                    }
                    File.Delete(jsonlFile);
                }
            }
        }
        catch
        {
        }
    }

    public void LogSessionStart(string tool, string command, string cwd, int pid, short cols, short rows)
    {
        _startedAtUtc = DateTime.UtcNow;
        JsonObject obj = CreateEvent("session_start");
        obj["tool"] = tool;
        obj["command"] = command;
        obj["cwd"] = cwd;
        obj["pid"] = pid;
        obj["cols"] = cols;
        obj["rows"] = rows;
        WriteEvent(obj);
    }

    public void LogInput(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        JsonObject obj = CreateEvent("input");
        obj["text"] = SecretRedactor.Redact(text);
        WriteEvent(obj);
    }

    public void LogOutput(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        string stripped = StripAnsi(text);
        if (string.IsNullOrEmpty(stripped))
        {
            return;
        }

        JsonObject obj = CreateEvent("output");
        obj["text"] = SecretRedactor.Redact(stripped);
        WriteEvent(obj);
    }

    public void LogSessionEnd(int exitCode)
    {
        long durationMs = (long)(DateTime.UtcNow - _startedAtUtc).TotalMilliseconds;
        JsonObject obj = CreateEvent("session_end");
        obj["exit_code"] = exitCode;
        obj["duration_ms"] = durationMs;
        WriteEvent(obj);
    }

    public void Dispose()
    {
        lock (_writeLock)
        {
            _disposed = true;
            try
            {
                _writer?.Flush();
                _writer?.Dispose();
            }
            catch
            {
            }
            finally
            {
                _writer = null;
            }
        }
    }

    internal static string StripAnsi(string text)
    {
        // Replace cursor-right/column sequences with space so adjacent text isn't concatenated.
        string prepped = CursorRightPattern().Replace(text, " ");
        // Normalize CRLF, strip remaining ANSI, strip terminal UI chars, normalize NBSP.
        string stripped = AnsiPattern().Replace(prepped, string.Empty).Replace("\r\n", "\n");
        stripped = UiCharsPattern().Replace(stripped, string.Empty);
        stripped = stripped.Replace(' ', ' ');
        // Collapse runs of spaces left by cursor replacements or terminal width padding.
        stripped = MultiSpacePattern().Replace(stripped, " ");

        // Apply \b and \r as terminal control characters.
        var buf = new System.Text.StringBuilder(stripped.Length);
        int lineStart = 0;
        foreach (char c in stripped)
        {
            if (c == '\r')
            {
                buf.Length = lineStart;
            }
            else if (c == '\b')
            {
                if (buf.Length > lineStart) buf.Length--;
            }
            else if (c == '\n')
            {
                buf.Append('\n');
                lineStart = buf.Length;
            }
            else
            {
                buf.Append(c);
            }
        }

        // Collapse blank lines; trim trailing whitespace per line.
        string[] lines = buf.ToString().Split('\n');
        var result = new System.Text.StringBuilder(buf.Length);
        int blankRun = 0;
        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || ProgressLinePattern().IsMatch(trimmed))
            {
                blankRun++;
                if (blankRun <= 1) result.Append('\n');
            }
            else
            {
                blankRun = 0;
                result.Append(trimmed);
                result.Append('\n');
            }
        }

        string final = result.ToString().Trim('\n');
        return final.Length > 0 ? final + "\n" : string.Empty;
    }

    internal static string DetectTool(string commandLine)
    {
        string lower = commandLine.Trim().ToLowerInvariant();
        string fileName;
        try
        {
            (string parsed, _) = TerminalCommandLine.SplitCommandLine(commandLine);
            fileName = Path.GetFileNameWithoutExtension(parsed).ToLowerInvariant();
        }
        catch
        {
            fileName = Path.GetFileNameWithoutExtension(lower.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty);
        }

        return fileName switch
        {
            "claude" => "claude-code",
            "codex" => "codex",
            _ when lower.Contains("gh") && lower.Contains("copilot") => "gh-copilot",
            _ => fileName.Length > 0 ? fileName : "unknown"
        };
    }

    private JsonObject CreateEvent(string eventType) => new()
    {
        ["ts"] = DateTimeOffset.Now.ToString("o"),
        ["sid"] = _sid,
        ["event"] = eventType
    };

    private void WriteEvent(JsonObject obj)
    {
        lock (_writeLock)
        {
            if (_disposed || _writer is null)
            {
                return;
            }

            try
            {
                _writer.WriteLine(obj.ToJsonString());
                _writer.Flush();
            }
            catch
            {
            }
        }
    }

    private static string ResolveBaseLogDirectory(string? logDirectoryOverride) =>
        string.IsNullOrWhiteSpace(logDirectoryOverride)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ConPtyTerminal", "logs", "sessions")
            : logDirectoryOverride;

    private static string ResolveProjectName(string workingDirectory)
    {
        string trimmed = workingDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? "unknown" : name;
    }
}
