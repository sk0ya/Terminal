using System.IO;
using System.Text.Json.Nodes;

using Terminal.Logging;

namespace Terminal.Tests;

public class SessionLoggingTests
{
    [Theory]
    [InlineData("claude", "claude-code")]
    [InlineData("claude.exe", "claude-code")]
    [InlineData("codex", "codex")]
    [InlineData("codex.exe", "codex")]
    [InlineData("cmd.exe /K", "cmd")]
    [InlineData("pwsh -NoLogo", "pwsh")]
    [InlineData("gh copilot suggest", "gh-copilot")]
    public void DetectTool_ReturnsExpected(string commandLine, string expected)
    {
        string result = SessionLogWriter.DetectTool(commandLine);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void StripAnsi_RemovesColorCodes()
    {
        string input = "\x1b[32mHello\x1b[0m World";
        string result = SessionLogWriter.StripAnsi(input);
        Assert.Equal("Hello World\n", result);
    }

    [Fact]
    public void StripAnsi_RemovesCursorMovement()
    {
        string input = "\x1b[2J\x1b[HHello";
        string result = SessionLogWriter.StripAnsi(input);
        Assert.Equal("Hello\n", result);
    }

    [Fact]
    public void StripAnsi_NormalizesLineEndings()
    {
        // \r\n -> \n, lone \r discards current line (carriage return overwrites)
        string input = "line1\r\nline2\rline3\n";
        string result = SessionLogWriter.StripAnsi(input);
        Assert.Equal("line1\nline3\n", result);
    }

    [Fact]
    public void StripAnsi_HandlesBackspace()
    {
        string result = SessionLogWriter.StripAnsi("helo\blo");
        Assert.Equal("hello\n", result);
    }

    [Fact]
    public void StripAnsi_StripsBoxChars()
    {
        // Box drawing chars are stripped; the resulting empty line collapses to one blank line.
        string input = "output\n────────────────\nmore output\n";
        string result = SessionLogWriter.StripAnsi(input);
        Assert.Equal("output\n\nmore output\n", result);
    }

    [Fact]
    public void StripAnsi_StripsBlockElements()
    {
        // Leading space left after block chars are removed is also trimmed.
        string result = SessionLogWriter.StripAnsi(" ▐▛Claude Code v1.0");
        Assert.Equal("Claude Code v1.0\n", result);
    }

    [Fact]
    public void StripAnsi_InsertSpaceForCursorRight()
    {
        // CHA (\x1b[nG) is replaced with space so adjacent words are separated.
        string result = SessionLogWriter.StripAnsi("Claude\x1b[8GCode\x1b[14G v1.0");
        Assert.Equal("Claude Code v1.0\n", result);
    }

    [Fact]
    public void StripAnsi_CollapsesMultipleSpaces()
    {
        string result = SessionLogWriter.StripAnsi("hello     world");
        Assert.Equal("hello world\n", result);
    }

    [Fact]
    public void StripAnsi_TrimsLeadingAndTrailingSpaces()
    {
        string result = SessionLogWriter.StripAnsi("  hello world  \n");
        Assert.Equal("hello world\n", result);
    }

    [Theory]
    [InlineData("! ls Running…")]           // ellipsis-terminated progress
    [InlineData("bypass permissions on (shift+tab to cycle)medium·/effort")] // status bar
    [InlineData("esc to interrupt")]             // interrupt hint
    [InlineData(">")]                            // bare Claude Code prompt
    [InlineData("  >  ")]                        // prompt with whitespace
    public void StripAnsi_FiltersProgressLines(string progressLine)
    {
        string result = SessionLogWriter.StripAnsi(progressLine);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void StripAnsi_KeepsContentAroundProgressLines()
    {
        string input = "real content\n! ls Running…\nmore content\n";
        string result = SessionLogWriter.StripAnsi(input);
        Assert.Equal("real content\n\nmore content\n", result);
    }

    [Fact]
    public void StripAnsi_NormalizesNbsp()
    {
        // NBSP (U+00A0) is normalized to regular space and then trimmed.
        string result = SessionLogWriter.StripAnsi(">  ");
        Assert.Equal(">\n", result);
    }

    [Fact]
    public void StripAnsi_RemovesOscSequences()
    {
        string input = "\x1b]0;Terminal Title\x07Hello";
        string result = SessionLogWriter.StripAnsi(input);
        Assert.Equal("Hello\n", result);
    }

    [Fact]
    public void StripAnsi_PlainText_IsUnchanged()
    {
        string input = "Hello, World!\n";
        string result = SessionLogWriter.StripAnsi(input);
        Assert.Equal(input, result);
    }

    [Fact]
    public void SessionLogWriter_WritesValidJsonl()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            string filePath;
            using (SessionLogWriter writer = SessionLogWriter.Create("claude", tempDir, tempDir))
            {
                writer.LogSessionStart("claude-code", "claude", tempDir, 1234, 220, 50);
                writer.LogInput("hello world\n");
                writer.LogOutput("Hello! How can I help?\n");
                writer.LogSessionEnd(0);
                filePath = Directory.GetFiles(tempDir, "*.jsonl", SearchOption.AllDirectories).Single();
            }

            string[] lines = File.ReadAllLines(filePath);
            Assert.Equal(4, lines.Length);

            foreach (string line in lines)
            {
                JsonNode? node = JsonNode.Parse(line);
                Assert.NotNull(node);
                Assert.NotNull(node["ts"]);
                Assert.NotNull(node["sid"]);
                Assert.NotNull(node["event"]);
            }

            Assert.Contains("\"event\":\"session_start\"", lines[0]);
            Assert.Contains("\"event\":\"input\"", lines[1]);
            Assert.Contains("\"event\":\"output\"", lines[2]);
            Assert.Contains("\"event\":\"session_end\"", lines[3]);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void SessionLogWriter_EmptyOutputNotLogged()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            string filePath;
            using (SessionLogWriter writer = SessionLogWriter.Create("claude", tempDir, tempDir))
            {
                writer.LogSessionStart("claude-code", "claude", tempDir, 0, 80, 24);
                writer.LogOutput("\x1b[32m\x1b[0m");
                writer.LogSessionEnd(0);
                filePath = Directory.GetFiles(tempDir, "*.jsonl", SearchOption.AllDirectories).Single();
            }

            string[] lines = File.ReadAllLines(filePath);
            Assert.Equal(2, lines.Length);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void SessionLogWriter_SessionStartHasExpectedFields()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            string filePath;
            using (SessionLogWriter writer = SessionLogWriter.Create("claude", tempDir, tempDir))
            {
                writer.LogSessionStart("claude-code", "claude", @"C:\Projects\Terminal", 5678, (short)220, (short)50);
                filePath = Directory.GetFiles(tempDir, "*.jsonl", SearchOption.AllDirectories).Single();
            }

            string line = File.ReadAllLines(filePath)[0];
            JsonNode node = JsonNode.Parse(line)!;

            Assert.Equal("session_start", node["event"]!.GetValue<string>());
            Assert.Equal("claude-code", node["tool"]!.GetValue<string>());
            Assert.Equal("claude", node["command"]!.GetValue<string>());
            Assert.Equal(5678, node["pid"]!.GetValue<int>());
            Assert.Equal(220, node["cols"]!.GetValue<int>());
            Assert.Equal(50, node["rows"]!.GetValue<int>());
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void SessionLogWriter_SecretInOutputIsRedacted()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            string secret = "sk-" + new string('x', 25);
            string filePath;
            using (SessionLogWriter writer = SessionLogWriter.Create("claude", tempDir, tempDir))
            {
                writer.LogOutput($"Using key {secret} to call API\n");
                filePath = Directory.GetFiles(tempDir, "*.jsonl", SearchOption.AllDirectories).Single();
            }

            string content = File.ReadAllText(filePath);
            Assert.DoesNotContain(secret, content);
            Assert.Contains("[REDACTED]", content);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
