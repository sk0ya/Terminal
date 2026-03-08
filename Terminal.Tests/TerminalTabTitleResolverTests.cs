namespace Terminal.Tests;

public sealed class TerminalTabTitleResolverTests
{
    [Fact]
    public void ResolvePrefersProfileDisplayNameForKnownProfiles()
    {
        TerminalProfileDefinition profile = new(
            "pwsh",
            "PowerShell 7",
            "pwsh.exe -NoLogo",
            "PowerShell");

        string title = TerminalTabTitleResolver.Resolve(
            terminalTitle: @"C:\Projects\Term3",
            commandLine: "pwsh.exe -NoLogo",
            profile);

        Assert.Equal("PowerShell 7", title);
    }

    [Fact]
    public void ResolveUsesExecutableNameForCustomCommands()
    {
        TerminalProfileDefinition profile = new(
            "custom",
            "Custom",
            string.Empty,
            "Custom",
            IsCustom: true);

        string title = TerminalTabTitleResolver.Resolve(
            terminalTitle: @"C:\Projects\Term3",
            commandLine: "\"C:\\Users\\koya\\AppData\\Local\\Programs\\claude\\claude.exe\"",
            profile);

        Assert.Equal("claude", title);
    }

    [Fact]
    public void ResolvePrefersMeaningfulDynamicTitleOverFallback()
    {
        TerminalProfileDefinition profile = new(
            "custom",
            "Custom",
            string.Empty,
            "Custom",
            IsCustom: true);

        string title = TerminalTabTitleResolver.Resolve(
            terminalTitle: "Claude Code - PowerShell 7",
            commandLine: "pwsh.exe -NoLogo",
            profile);

        Assert.Equal("Claude Code", title);
    }

    [Fact]
    public void ResolveStripsAdministratorPrefixFromDynamicTitle()
    {
        TerminalProfileDefinition profile = new(
            "custom",
            "Custom",
            string.Empty,
            "Custom",
            IsCustom: true);

        string title = TerminalTabTitleResolver.Resolve(
            terminalTitle: "Administrator: Claude Code",
            commandLine: "claude.exe",
            profile);

        Assert.Equal("Claude Code", title);
    }
}
