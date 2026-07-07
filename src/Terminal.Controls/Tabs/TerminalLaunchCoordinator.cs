using System.Diagnostics.CodeAnalysis;
using System.IO;

using Terminal.Settings;

namespace Terminal.Tabs;

internal enum TerminalLaunchInputKey
{
    Other,
    Enter
}

internal enum TerminalLaunchInputAction
{
    None,
    Start
}

internal sealed record TerminalLaunchRequest(string CommandLine, string WorkingDirectory);

internal sealed class TerminalLaunchCoordinator
{
    private string _workingDirectoryInput = string.Empty;

    public TerminalLaunchCoordinator(IReadOnlyList<TerminalProfileDefinition> profiles)
    {
        CustomProfile = new(
            "custom", "Custom", string.Empty,
            "Use any executable or shell command line.", IsCustom: true);
        Profiles = [.. profiles, CustomProfile];
        SelectedProfile = CustomProfile;
        CommandLine = string.Empty;
        WorkingDirectory = string.Empty;
        ActiveWorkingDirectory = string.Empty;
    }

    public IReadOnlyList<TerminalProfileDefinition> Profiles { get; }
    public TerminalProfileDefinition CustomProfile { get; }
    public TerminalProfileDefinition SelectedProfile { get; private set; }
    public string CommandLine { get; private set; }
    public string WorkingDirectory { get; private set; }
    public string ActiveCommandLine { get; private set; } = string.Empty;
    public string ActiveWorkingDirectory { get; private set; }
    public string ProfileHint => SelectedProfile.Description;

    public static TerminalLaunchInputAction ResolveInput(
        Func<TerminalLaunchInputKey> resolveKey,
        bool hasSession,
        bool isTransitionActive,
        bool isRecovering,
        bool isClosing)
    {
        if (hasSession || isTransitionActive || isRecovering || isClosing)
        {
            return TerminalLaunchInputAction.None;
        }

        return resolveKey() == TerminalLaunchInputKey.Enter
            ? TerminalLaunchInputAction.Start
            : TerminalLaunchInputAction.None;
    }

    public void Apply(
        string? profileId,
        string? commandLine,
        string? workingDirectory,
        string currentWorkingDirectory)
    {
        SelectedProfile = TerminalProfileCatalog.ResolveSelectedProfile(
            Profiles, CustomProfile, profileId, commandLine);
        CommandLine = string.IsNullOrWhiteSpace(commandLine) && !SelectedProfile.IsCustom
            ? SelectedProfile.CommandLine
            : commandLine ?? string.Empty;
        _workingDirectoryInput = workingDirectory ?? string.Empty;
        WorkingDirectory = EffectiveWorkingDirectory(_workingDirectoryInput, currentWorkingDirectory);
    }

    public string SelectProfile(TerminalProfileDefinition? profile)
    {
        SelectedProfile = profile ?? CustomProfile;
        if (!SelectedProfile.IsCustom && !string.IsNullOrWhiteSpace(SelectedProfile.CommandLine))
        {
            CommandLine = SelectedProfile.CommandLine;
        }

        return CommandLine;
    }

    public TerminalProfileDefinition UpdateCommandLine(string? commandLine)
    {
        CommandLine = commandLine ?? string.Empty;
        SelectedProfile = TerminalProfileCatalog.MatchProfileByCommandLine(Profiles, commandLine) ?? CustomProfile;
        return SelectedProfile;
    }

    public void UpdateWorkingDirectory(string? workingDirectory, string currentWorkingDirectory)
    {
        _workingDirectoryInput = workingDirectory ?? string.Empty;
        WorkingDirectory = EffectiveWorkingDirectory(_workingDirectoryInput, currentWorkingDirectory);
    }

    public string GetEffectiveCommandLine(string defaultCommandLine) =>
        EffectiveCommandLine(CommandLine, defaultCommandLine);

    public string GetEffectiveWorkingDirectory(string currentWorkingDirectory) =>
        EffectiveWorkingDirectory(_workingDirectoryInput, currentWorkingDirectory);

    public bool TryBuildLaunchRequest(
        string? commandLine,
        string? workingDirectory,
        string defaultCommandLine,
        string currentWorkingDirectory,
        Func<string, string> expandVariables,
        Func<string, string> getFullPath,
        Func<string, bool> directoryExists,
        out TerminalLaunchRequest? request,
        out Exception? error)
    {
        try
        {
            string candidate = EffectiveWorkingDirectory(workingDirectory, currentWorkingDirectory);
            string normalizedDirectory = getFullPath(expandVariables(candidate));
            if (!directoryExists(normalizedDirectory))
            {
                throw new DirectoryNotFoundException(normalizedDirectory);
            }

            CommandLine = commandLine ?? string.Empty;
            WorkingDirectory = normalizedDirectory;
            _workingDirectoryInput = normalizedDirectory;
            request = new TerminalLaunchRequest(
                EffectiveCommandLine(CommandLine, defaultCommandLine),
                WorkingDirectory);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            request = null;
            error = ex;
            return false;
        }
    }

    public void Activate(string commandLine, string workingDirectory)
    {
        ActiveCommandLine = commandLine;
        ActiveWorkingDirectory = workingDirectory;
    }

    public void ClearActive(string currentWorkingDirectory)
    {
        ActiveCommandLine = string.Empty;
        ActiveWorkingDirectory = currentWorkingDirectory;
    }

    public void UpdateActiveWorkingDirectory(string workingDirectory) =>
        ActiveWorkingDirectory = workingDirectory;

    public bool TryUpdateActiveWorkingDirectory(
        string path,
        string currentWorkingDirectory,
        Func<string, string> getFullPath,
        [NotNullWhen(true)] out string? canonicalPath)
    {
        try
        {
            canonicalPath = getFullPath(path);
        }
        catch
        {
            canonicalPath = null;
            return false;
        }

        UpdateActiveWorkingDirectory(canonicalPath);
        UpdateWorkingDirectory(canonicalPath, currentWorkingDirectory);
        return true;
    }

    private static string EffectiveCommandLine(string? value, string defaultCommandLine) =>
        string.IsNullOrWhiteSpace(value) ? defaultCommandLine : value.Trim();

    private static string EffectiveWorkingDirectory(string? value, string currentWorkingDirectory) =>
        string.IsNullOrWhiteSpace(value) ? currentWorkingDirectory : value.Trim();
}
