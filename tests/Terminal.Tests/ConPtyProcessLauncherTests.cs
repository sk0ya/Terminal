using Microsoft.Win32.SafeHandles;
using Terminal.Sessions;

namespace Terminal.Tests;

public sealed class ConPtyProcessLauncherTests
{
    [Fact]
    public void LaunchTransfersProcessAndJobAndReleasesConsoleEndpoints()
    {
        var api = new FakeApi();
        var owner = CreateOwner();

        int processId = new ConPtyProcessLauncher(api).Launch("cmd.exe", null, null, owner);

        Assert.Equal(42, processId);
        Assert.Equal((IntPtr)2, owner.Process);
        Assert.Equal((IntPtr)3, owner.Thread);
        Assert.Equal((IntPtr)4, owner.Job);
        Assert.Equal([(IntPtr)1], api.DeletedAttributeLists);
        Assert.Empty(api.ClosedHandles);
    }

    [Fact]
    public void JobAssignmentFailureClosesEveryUntransferredNativeHandle()
    {
        var api = new FakeApi { FailAssignment = true };
        var owner = CreateOwner();

        Assert.Throws<InvalidOperationException>(() =>
            new ConPtyProcessLauncher(api).Launch("cmd.exe", null, null, owner));

        Assert.Equal([(IntPtr)4, (IntPtr)3, (IntPtr)2], api.ClosedHandles);
        Assert.Equal([(IntPtr)1], api.DeletedAttributeLists);
        Assert.Equal(IntPtr.Zero, owner.Process);
    }

    private static ConPtyHandleOwner CreateOwner()
    {
        var owner = new ConPtyHandleOwner();
        owner.SetPseudoConsole(
            (IntPtr)9,
            new SafeFileHandle(IntPtr.Zero, false),
            new SafeFileHandle(IntPtr.Zero, false),
            new SafeFileHandle(IntPtr.Zero, false),
            new SafeFileHandle(IntPtr.Zero, false));
        return owner;
    }

    private sealed class FakeApi : IConPtyProcessApi
    {
        public bool FailAssignment { get; init; }
        public List<IntPtr> ClosedHandles { get; } = [];
        public List<IntPtr> DeletedAttributeLists { get; } = [];
        public IntPtr CreateAttributeList(IntPtr pseudoConsole) => (IntPtr)1;
        public ProcessLaunchResult CreateProcess(string commandLine, string? workingDirectory, IntPtr environmentBlock, uint flags, IntPtr attributeList, int startupFlags) => new((IntPtr)2, (IntPtr)3, 42);
        public bool IsProcessInJob(IntPtr process) => false;
        public IntPtr CreateKillOnCloseJob() => (IntPtr)4;
        public void AssignProcessToJob(IntPtr job, IntPtr process)
        {
            if (FailAssignment) throw new InvalidOperationException("injected assignment failure");
        }
        public void DeleteAttributeList(IntPtr attributeList) { if (attributeList != IntPtr.Zero) DeletedAttributeLists.Add(attributeList); }
        public void CloseHandle(IntPtr handle) { if (handle != IntPtr.Zero) ClosedHandles.Add(handle); }
    }
}
