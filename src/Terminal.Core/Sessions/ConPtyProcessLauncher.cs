using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Terminal.Sessions;

internal sealed class ConPtyProcessLauncher
{
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const int StartfUseStdHandles = 0x00000100;
    private const int ProcThreadAttributePseudoConsole = 0x00020016;
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

    private readonly IConPtyProcessApi _api;

    internal ConPtyProcessLauncher(IConPtyProcessApi api) => _api = api;

    internal int Launch(
        string commandLine,
        string? workingDirectory,
        IReadOnlyDictionary<string, string?>? environmentVariables,
        ConPtyHandleOwner owner)
    {
        IntPtr attributeList = IntPtr.Zero;
        IntPtr environmentBlock = IntPtr.Zero;
        IntPtr process = IntPtr.Zero;
        IntPtr thread = IntPtr.Zero;
        IntPtr job = IntPtr.Zero;

        try
        {
            attributeList = _api.CreateAttributeList(owner.PseudoConsole);
            environmentBlock = AllocateEnvironmentBlock(environmentVariables);
            uint flags = ExtendedStartupInfoPresent | (environmentBlock != IntPtr.Zero ? CreateUnicodeEnvironment : 0);
            ProcessLaunchResult result = _api.CreateProcess(
                commandLine,
                workingDirectory,
                environmentBlock,
                flags,
                attributeList,
                StartfUseStdHandles);
            process = result.Process;
            thread = result.Thread;

            if (!_api.IsProcessInJob(process))
            {
                job = _api.CreateKillOnCloseJob();
                _api.AssignProcessToJob(job, process);
            }

            owner.SetProcess(process, thread);
            process = IntPtr.Zero;
            thread = IntPtr.Zero;
            if (job != IntPtr.Zero)
            {
                owner.SetJob(job);
                job = IntPtr.Zero;
            }

            owner.ReleasePseudoConsoleEndpoints();
            return result.ProcessId;
        }
        finally
        {
            _api.CloseHandle(job);
            _api.CloseHandle(thread);
            _api.CloseHandle(process);
            _api.DeleteAttributeList(attributeList);
            if (environmentBlock != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(environmentBlock);
            }
        }
    }

    private static IntPtr AllocateEnvironmentBlock(IReadOnlyDictionary<string, string?>? overrides)
    {
        string[] variables = ConPtyProcessEnvironment.Build(overrides);
        return variables.Length == 0
            ? IntPtr.Zero
            : Marshal.StringToHGlobalUni(string.Join('\0', variables) + "\0\0");
    }
}

internal readonly record struct ProcessLaunchResult(IntPtr Process, IntPtr Thread, int ProcessId);

internal interface IConPtyProcessApi
{
    IntPtr CreateAttributeList(IntPtr pseudoConsole);
    ProcessLaunchResult CreateProcess(string commandLine, string? workingDirectory, IntPtr environmentBlock, uint flags, IntPtr attributeList, int startupFlags);
    bool IsProcessInJob(IntPtr process);
    IntPtr CreateKillOnCloseJob();
    void AssignProcessToJob(IntPtr job, IntPtr process);
    void DeleteAttributeList(IntPtr attributeList);
    void CloseHandle(IntPtr handle);
}

internal sealed class WindowsConPtyProcessApi : IConPtyProcessApi
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

    internal static WindowsConPtyProcessApi Instance { get; } = new();

    public IntPtr CreateAttributeList(IntPtr pseudoConsole)
    {
        IntPtr size = IntPtr.Zero;
        _ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        if (size == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to get attribute list size.");
        }

        IntPtr list = Marshal.AllocHGlobal(size);
        try
        {
            if (!InitializeProcThreadAttributeList(list, 1, 0, ref size)
                || !UpdateProcThreadAttribute(list, 0, (IntPtr)0x00020016, pseudoConsole,
                    (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to initialize process attributes.");
            }

            return list;
        }
        catch
        {
            Marshal.FreeHGlobal(list);
            throw;
        }
    }

    public ProcessLaunchResult CreateProcess(string commandLine, string? workingDirectory, IntPtr environmentBlock, uint flags, IntPtr attributeList, int startupFlags)
    {
        var startup = new StartupInfoEx
        {
            StartupInfo = new StartupInfo { cb = Marshal.SizeOf<StartupInfoEx>(), dwFlags = startupFlags },
            lpAttributeList = attributeList
        };
        if (!CreateProcessW(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false,
                flags, environmentBlock, workingDirectory, ref startup, out ProcessInformation process))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Failed to launch command: {commandLine}");
        }

        return new ProcessLaunchResult(process.hProcess, process.hThread, process.dwProcessId);
    }

    public bool IsProcessInJob(IntPtr process)
    {
        if (!IsProcessInJobNative(process, IntPtr.Zero, out bool result))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to query process job membership.");
        }
        return result;
    }

    public IntPtr CreateKillOnCloseJob()
    {
        IntPtr job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create process job.");
        }
        var info = new JobObjectExtendedLimitInformationState
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose }
        };
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info,
                (uint)Marshal.SizeOf<JobObjectExtendedLimitInformationState>()))
        {
            CloseHandle(job);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to configure process job.");
        }
        return job;
    }

    public void AssignProcessToJob(IntPtr job, IntPtr process)
    {
        if (!AssignProcessToJobObject(job, process))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to assign process to job.");
        }
    }

    public void DeleteAttributeList(IntPtr list)
    {
        if (list == IntPtr.Zero) return;
        DeleteProcThreadAttributeList(list);
        Marshal.FreeHGlobal(list);
    }

    public void CloseHandle(IntPtr handle) { if (handle != IntPtr.Zero) _ = CloseHandleNative(handle); }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool CreateProcessW(string? app, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string? directory, ref StartupInfoEx startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "IsProcessInJob")] private static extern bool IsProcessInJobNative(IntPtr process, IntPtr job, out bool result);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JobObjectExtendedLimitInformationState info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "CloseHandle")] private static extern bool CloseHandleNative(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo { public int cb; public string? lpReserved; public string? lpDesktop; public string? lpTitle; public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags; public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError; }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr lpAttributeList; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct JobObjectBasicLimitInformation { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] private struct JobObjectExtendedLimitInformationState { public JobObjectBasicLimitInformation BasicLimitInformation; public IoCounters IoInfo; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
}
