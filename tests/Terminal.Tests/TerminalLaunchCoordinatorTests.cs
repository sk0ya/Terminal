using System.IO;

using Terminal.Settings;
using Terminal.Tabs;

namespace Terminal.Tests;

public sealed class TerminalLaunchCoordinatorTests
{
    private static readonly TerminalProfileDefinition CmdProfile = new(
        "cmd", "Command Prompt", "cmd.exe /K", "Command profile");
    private static readonly TerminalProfileDefinition PwshProfile = new(
        "pwsh", "PowerShell", "pwsh.exe -NoLogo", "PowerShell profile");

    [Fact]
    public void ApplyAndCommandEditsMatchProfilesOrSelectCustom()
    {
        var coordinator = CreateCoordinator();

        coordinator.Apply("cmd", "cmd.exe /K", "C:\\work", "C:\\current");
        Assert.Equal(CmdProfile, coordinator.SelectedProfile);
        Assert.Equal("Command profile", coordinator.ProfileHint);

        coordinator.UpdateCommandLine("tool.exe --custom");
        Assert.True(coordinator.SelectedProfile.IsCustom);
        Assert.Equal("tool.exe --custom", coordinator.CommandLine);

        coordinator.UpdateCommandLine(" ");
        Assert.True(coordinator.SelectedProfile.IsCustom);
        Assert.Equal(" ", coordinator.CommandLine);
        Assert.Equal("cmd.exe /K", coordinator.GetEffectiveCommandLine("cmd.exe /K"));
    }

    [Fact]
    public void ApplyResolvesProfileFromRawBlankCommandBeforeChoosingDisplayCommand()
    {
        var coordinator = CreateCoordinator();

        coordinator.Apply("pwsh", " ", null, "C:\\current");
        Assert.Equal(PwshProfile, coordinator.SelectedProfile);
        Assert.Equal("pwsh.exe -NoLogo", coordinator.CommandLine);

        coordinator.Apply("custom", " ", null, "C:\\current");
        Assert.True(coordinator.SelectedProfile.IsCustom);
        Assert.Equal(" ", coordinator.CommandLine);

        coordinator.Apply("missing", null, null, "C:\\current");
        Assert.True(coordinator.SelectedProfile.IsCustom);
        Assert.Empty(coordinator.CommandLine);
    }

    [Fact]
    public void SelectingProfileUpdatesCommandLine()
    {
        var coordinator = CreateCoordinator();

        Assert.Equal("cmd.exe /K", coordinator.SelectProfile(CmdProfile));
        Assert.Equal("cmd", coordinator.SelectedProfile.Id);
    }

    [Fact]
    public void BlankLaunchValuesUseDefaults()
    {
        var coordinator = CreateCoordinator();

        bool success = coordinator.TryBuildLaunchRequest(
            "  ", " ", "cmd.exe /K", "C:\\default", value => value, value => value, value => value == "C:\\default",
            out TerminalLaunchRequest? request, out Exception? error);

        Assert.True(success);
        Assert.Null(error);
        Assert.Equal(new TerminalLaunchRequest("cmd.exe /K", "C:\\default"), request);
    }

    [Fact]
    public void BlankCommandUsesDefaultValueAtEachOperation()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateCommandLine(" ");

        Assert.Equal("default-first.exe", coordinator.GetEffectiveCommandLine("default-first.exe"));

        bool success = coordinator.TryBuildLaunchRequest(
            " ", "C:\\work", "default-second.exe", "C:\\current",
            value => value, value => value, _ => true,
            out TerminalLaunchRequest? request, out _);

        Assert.True(success);
        Assert.Equal("default-second.exe", request!.CommandLine);
    }

    [Fact]
    public void InvalidWorkingDirectoryRejectsLaunch()
    {
        var coordinator = CreateCoordinator();

        bool success = coordinator.TryBuildLaunchRequest(
            "tool.exe", "C:\\missing", "cmd.exe /K", "C:\\current", value => value, value => value, _ => false,
            out TerminalLaunchRequest? request, out Exception? error);

        Assert.False(success);
        Assert.Null(request);
        Assert.IsType<DirectoryNotFoundException>(error);
    }

    [Fact]
    public void BlankWorkingDirectoryUsesCurrentValueAtEachOperation()
    {
        var coordinator = CreateCoordinator();
        coordinator.Apply("custom", "tool.exe", " ", "C:\\first");

        Assert.Equal("C:\\second", coordinator.GetEffectiveWorkingDirectory("C:\\second"));

        bool success = coordinator.TryBuildLaunchRequest(
            "tool.exe", " ", "cmd.exe /K", "C:\\third", value => value, value => value, _ => true,
            out TerminalLaunchRequest? request, out _);

        Assert.True(success);
        Assert.Equal("C:\\third", request!.WorkingDirectory);
    }

    [Fact]
    public void ActiveLaunchStateCanBeSetUpdatedAndCleared()
    {
        var coordinator = CreateCoordinator();

        coordinator.Activate("tool.exe", "C:\\work");
        coordinator.UpdateActiveWorkingDirectory("C:\\work\\child");
        Assert.Equal("tool.exe", coordinator.ActiveCommandLine);
        Assert.Equal("C:\\work\\child", coordinator.ActiveWorkingDirectory);

        coordinator.ClearActive("C:\\current");
        Assert.Empty(coordinator.ActiveCommandLine);
        Assert.Equal("C:\\current", coordinator.ActiveWorkingDirectory);
    }

    [Fact]
    public void IdleEnterResolvesStartAndEvaluatesKeyOnce()
    {
        int calls = 0;

        TerminalLaunchInputAction action = TerminalLaunchCoordinator.ResolveInput(
            () =>
            {
                calls++;
                return TerminalLaunchInputKey.Enter;
            },
            hasSession: false,
            isTransitionActive: false,
            isRecovering: false,
            isClosing: false);

        Assert.Equal(TerminalLaunchInputAction.Start, action);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void IdleOtherKeyDoesNotStartAndEvaluatesKeyOnce()
    {
        int calls = 0;

        TerminalLaunchInputAction action = TerminalLaunchCoordinator.ResolveInput(
            () =>
            {
                calls++;
                return TerminalLaunchInputKey.Other;
            },
            hasSession: false,
            isTransitionActive: false,
            isRecovering: false,
            isClosing: false);

        Assert.Equal(TerminalLaunchInputAction.None, action);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    [InlineData(true, true, true, true)]
    public void BusyStateDoesNotStartOrEvaluateKey(
        bool hasSession,
        bool isTransitionActive,
        bool isRecovering,
        bool isClosing)
    {
        int calls = 0;

        TerminalLaunchInputAction action = TerminalLaunchCoordinator.ResolveInput(
            () =>
            {
                calls++;
                return TerminalLaunchInputKey.Enter;
            },
            hasSession,
            isTransitionActive,
            isRecovering,
            isClosing);

        Assert.Equal(TerminalLaunchInputAction.None, action);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void BusyStateShortCircuitsThrowingKeyResolver()
    {
        TerminalLaunchInputAction action = TerminalLaunchCoordinator.ResolveInput(
            () => throw new InvalidOperationException("key resolver must not run"),
            hasSession: true,
            isTransitionActive: false,
            isRecovering: false,
            isClosing: false);

        Assert.Equal(TerminalLaunchInputAction.None, action);
    }

    private static TerminalLaunchCoordinator CreateCoordinator() =>
        new([CmdProfile, PwshProfile]);
}
