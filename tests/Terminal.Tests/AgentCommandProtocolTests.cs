using Terminal.Tabs;

namespace Terminal.Tests;

public sealed class AgentCommandProtocolTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\PowerShell\\7\\pwsh.exe\" -NoLogo", (int)AgentShellKind.PowerShell)]
    [InlineData("powershell.exe -NoLogo", (int)AgentShellKind.PowerShell)]
    [InlineData("C:\\Program Files\\Git\\bin\\bash.exe --login -i", (int)AgentShellKind.Bash)]
    [InlineData("cmd.exe /K", (int)AgentShellKind.Cmd)]
    [InlineData("", (int)AgentShellKind.Unknown)]
    [InlineData("   ", (int)AgentShellKind.Unknown)]
    [InlineData("some-random-shell", (int)AgentShellKind.Unknown)]
    public void DetectShellKindMatchesKnownShells(string commandLine, int expected)
    {
        Assert.Equal((AgentShellKind)expected, AgentCommandProtocol.DetectShellKind(commandLine));
    }

    [Fact]
    public void NewMarkerIdIsUnique()
    {
        Assert.NotEqual(AgentCommandProtocol.NewMarkerId(), AgentCommandProtocol.NewMarkerId());
    }

    [Fact]
    public void BuildSentinelCommandWrapsCommandWithMarkers()
    {
        string line = AgentCommandProtocol.BuildSentinelCommand(AgentShellKind.PowerShell, "echo hello", "abc123");

        Assert.Contains("__ASE_B_abc123", line);
        Assert.Contains("echo hello", line);
        Assert.Contains("__ASE_E_abc123_", line);
        Assert.Contains("$LASTEXITCODE", line);
    }

    [Fact]
    public void BuildSentinelCommandForBashCapturesExitStatus()
    {
        string line = AgentCommandProtocol.BuildSentinelCommand(AgentShellKind.Bash, "false", "id");

        Assert.Contains("echo __ASE_B_id;", line);
        Assert.Contains("__aseec=$?;", line);
        Assert.Contains("echo __ASE_E_id_${__aseec}", line);
    }

    [Fact]
    public void BuildSentinelCommandForCmdWrapsWholeChainInDelayedExpansionChild()
    {
        string line = AgentCommandProtocol.BuildSentinelCommand(AgentShellKind.Cmd, "cmd /c exit 3", "id");

        // The entire chain must run inside a single /v:on child so !errorlevel! reflects
        // the command rather than the spawn of a separate child shell.
        Assert.StartsWith("cmd /v:on /c \"echo __ASE_B_id&", line);
        Assert.Contains("cmd /c exit 3", line);
        Assert.EndsWith("&echo __ASE_E_id_!errorlevel!\"", line);
    }

    [Fact]
    public void TryParseCompletedOutputIgnoresEndMarkerNotAtLineStart()
    {
        // A marker appearing mid-line (e.g. echoed input) must not be treated as the
        // printed completion marker.
        string text = "prefix __ASE_E_x_9 still running\r\n__ASE_B_x\r\nout\r\n__ASE_E_x_0\r\n";

        Assert.True(AgentCommandProtocol.TryParseCompletedOutput(text, "x", out string output, out int exitCode));
        Assert.Equal("out", output);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void TryParseCompletedOutputExtractsOutputAndExitCode()
    {
        // Mirrors what shows up in the terminal: the echoed input line (with markers as
        // un-evaluated text) followed by the printed marker lines around the output.
        string text =
            "Write-Output '__ASE_B_id'; echo hello; ...; Write-Output ('__ASE_E_id_' + ...)\r\n" +
            "__ASE_B_id\r\n" +
            "hello\r\n" +
            "__ASE_E_id_0\r\n";

        Assert.True(AgentCommandProtocol.TryParseCompletedOutput(text, "id", out string output, out int exitCode));
        Assert.Equal("hello", output);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void TryParseCompletedOutputCapturesNonZeroExitCode()
    {
        string text = "__ASE_B_x\r\nboom\r\n__ASE_E_x_3\r\n";

        Assert.True(AgentCommandProtocol.TryParseCompletedOutput(text, "x", out string output, out int exitCode));
        Assert.Equal("boom", output);
        Assert.Equal(3, exitCode);
    }

    [Fact]
    public void TryParseCompletedOutputHandlesEmptyOutput()
    {
        string text = "__ASE_B_x\r\n__ASE_E_x_0\r\n";

        Assert.True(AgentCommandProtocol.TryParseCompletedOutput(text, "x", out string output, out int exitCode));
        Assert.Equal(string.Empty, output);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void TryParseCompletedOutputPreservesMultilineOutput()
    {
        string text = "__ASE_B_x\r\nline1\r\nline2\r\n__ASE_E_x_0\r\n";

        Assert.True(AgentCommandProtocol.TryParseCompletedOutput(text, "x", out string output, out _));
        Assert.Equal("line1\r\nline2", output);
    }

    [Fact]
    public void TryParseCompletedOutputReturnsFalseWhileRunning()
    {
        // Only the BEGIN marker has been printed; the command has not finished.
        string text = "Write-Output '__ASE_B_x'; sleep 5; ...\r\n__ASE_B_x\r\npartial";

        Assert.False(AgentCommandProtocol.TryParseCompletedOutput(text, "x", out string output, out int exitCode));
        Assert.Equal(string.Empty, output);
        Assert.Equal(-1, exitCode);
    }

    [Fact]
    public void TryParseCompletedOutputToleratesTrailingSpacesAfterMarkers()
    {
        // cmd's "echo marker & ..." can leave a trailing space on the printed line.
        string text = "__ASE_B_x \r\noutput\r\n__ASE_E_x_0\r\n";

        Assert.True(AgentCommandProtocol.TryParseCompletedOutput(text, "x", out string output, out int exitCode));
        Assert.Equal("output", output);
        Assert.Equal(0, exitCode);
    }
}
