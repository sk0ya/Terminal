using System.ComponentModel;
using Terminal.Sessions;

namespace Terminal.Tests;

public sealed class ConPtyStartupDiagnosticsTests
{
    [Theory]
    [InlineData("cmd.exe", "cmd.exe")]
    [InlineData("\"C:\\Windows\\System32\\cmd.exe\" /K", "cmd.exe")]
    [InlineData("C:\\Windows\\System32\\cmd.exe /K", "cmd.exe")]
    [InlineData("powershell.exe -NoExit", "powershell.exe")]
    [InlineData("pwsh", "pwsh")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void ExtractExecutableName_ReturnsExpectedName(string commandLine, string expected)
    {
        Assert.Equal(expected, ConPtyStartupDiagnostics.ExtractExecutableName(commandLine));
    }

    [Fact]
    public void BuildDiagnosticHint_FileNotFound_ContainsExeName()
    {
        var ex = new Win32Exception(2);
        string hint = ConPtyStartupDiagnostics.BuildDiagnosticHint(ex, "notexist.exe");
        Assert.Contains("notexist.exe", hint, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(hint);
    }

    [Fact]
    public void BuildDiagnosticHint_FileNotFound_QuotedPath_ContainsExeName()
    {
        var ex = new Win32Exception(2);
        string hint = ConPtyStartupDiagnostics.BuildDiagnosticHint(ex, "\"C:\\Tools\\myshell.exe\" -arg");
        Assert.Contains("myshell.exe", hint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildDiagnosticHint_PathNotFound_MentionsPath()
    {
        var ex = new Win32Exception(3);
        string hint = ConPtyStartupDiagnostics.BuildDiagnosticHint(ex, "cmd.exe");
        Assert.Contains("path", hint, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(hint);
    }

    [Fact]
    public void BuildDiagnosticHint_AccessDenied_MentionsAccess()
    {
        var ex = new Win32Exception(5);
        string hint = ConPtyStartupDiagnostics.BuildDiagnosticHint(ex, "cmd.exe");
        Assert.Contains("access", hint, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(hint);
    }

    [Fact]
    public void BuildDiagnosticHint_InvalidParameter_MentionsParameter()
    {
        var ex = new Win32Exception(87);
        string hint = ConPtyStartupDiagnostics.BuildDiagnosticHint(ex, "cmd.exe");
        Assert.Contains("parameter", hint, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(hint);
    }

    [Fact]
    public void BuildDiagnosticHint_ElevationRequired_MentionsElevation()
    {
        var ex = new Win32Exception(740);
        string hint = ConPtyStartupDiagnostics.BuildDiagnosticHint(ex, "cmd.exe");
        Assert.Contains("elevation", hint, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(hint);
    }

    [Fact]
    public void BuildDiagnosticHint_UnknownErrorCode_ReturnsEmpty()
    {
        var ex = new Win32Exception(9999);
        string hint = ConPtyStartupDiagnostics.BuildDiagnosticHint(ex, "cmd.exe");
        Assert.Empty(hint);
    }

    [Fact]
    public void BuildDiagnosticHint_NonWin32Exception_ReturnsEmpty()
    {
        var ex = new InvalidOperationException("unexpected");
        string hint = ConPtyStartupDiagnostics.BuildDiagnosticHint(ex, "cmd.exe");
        Assert.Empty(hint);
    }
}
