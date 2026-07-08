using Microsoft.Win32.SafeHandles;
using Terminal.Sessions;

namespace Terminal.Tests;

public sealed class ConPtyPseudoConsoleFactoryTests
{
    [Fact]
    public void CreateBuildsConnectionAndTransfersAllHandlesToOwner()
    {
        var api = new FakeApi();
        var owner = new ConPtyHandleOwner();

        new ConPtyPseudoConsoleFactory(api).Create(120, 40, owner);

        Assert.Equal([(IntPtr)2, (IntPtr)3], api.InheritanceDisabled);
        Assert.Equal((120, 40, (IntPtr)1, (IntPtr)4), api.ConsoleRequest);
        Assert.Equal((IntPtr)5, owner.PseudoConsole);
        Assert.Equal((IntPtr)2, owner.InputWrite!.DangerousGetHandle());
        Assert.Equal((IntPtr)3, owner.OutputRead!.DangerousGetHandle());
        Assert.Empty(api.RawHandlesClosed);
        Assert.Empty(api.PseudoConsolesClosed);

        ConPtyOwnedHandles handles = Assert.IsType<ConPtyOwnedHandles>(owner.DetachForShutdown());
        handles.CloseCommunicationHandles(api.ClosePseudoConsole);
        Assert.Equal([(IntPtr)5], api.PseudoConsolesClosed);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 4)]
    [InlineData(5, 4)]
    public void FailureRecoversEveryHandleCreatedBeforeThatStage(int failureStage, int expectedClosed)
    {
        var api = new FakeApi { FailureStage = failureStage };
        var owner = new ConPtyHandleOwner();

        Assert.Throws<InvalidOperationException>(() =>
            new ConPtyPseudoConsoleFactory(api).Create(80, 24, owner));

        Assert.Equal(expectedClosed, api.RawHandlesClosed.Count);
        Assert.Equal(api.RawHandlesClosed.Count, api.RawHandlesClosed.Distinct().Count());
        Assert.Empty(api.PseudoConsolesClosed);
        Assert.Equal(IntPtr.Zero, owner.PseudoConsole);
        Assert.Null(owner.InputWrite);
    }

    [Fact]
    public void RejectedOwnershipTransferRecoversConsoleAndPipesExactlyOnce()
    {
        var api = new FakeApi();
        var owner = new ConPtyHandleOwner();
        Assert.NotNull(owner.DetachForShutdown());

        Assert.Throws<ObjectDisposedException>(() =>
            new ConPtyPseudoConsoleFactory(api).Create(80, 24, owner));

        Assert.Equal([(IntPtr)5], api.PseudoConsolesClosed);
        Assert.Empty(api.RawHandlesClosed);
        Assert.Equal(4, api.OwnedHandles.Count);
        Assert.All(api.OwnedHandles, static handle => Assert.True(handle.IsClosed));
    }

    private sealed class FakeApi : IConPtyPseudoConsoleApi
    {
        private int _stage;
        internal int FailureStage { get; init; }
        internal List<IntPtr> InheritanceDisabled { get; } = [];
        internal List<IntPtr> RawHandlesClosed { get; } = [];
        internal List<IntPtr> PseudoConsolesClosed { get; } = [];
        internal List<SafeFileHandle> OwnedHandles { get; } = [];
        internal (short Columns, short Rows, IntPtr Input, IntPtr Output) ConsoleRequest { get; private set; }

        public (IntPtr Read, IntPtr Write) CreatePipe(string direction)
        {
            FailIfRequested();
            return direction == "input" ? ((IntPtr)1, (IntPtr)2) : ((IntPtr)3, (IntPtr)4);
        }

        public void DisableInheritance(IntPtr handle, string direction)
        {
            FailIfRequested();
            InheritanceDisabled.Add(handle);
        }

        public IntPtr CreatePseudoConsole(short columns, short rows, IntPtr inputRead, IntPtr outputWrite)
        {
            FailIfRequested();
            ConsoleRequest = (columns, rows, inputRead, outputWrite);
            return (IntPtr)5;
        }

        public SafeFileHandle CreateOwnedPipeHandle(IntPtr handle)
        {
            var owned = new SafeFileHandle(handle, ownsHandle: false);
            OwnedHandles.Add(owned);
            return owned;
        }

        public void ClosePseudoConsole(IntPtr handle) => PseudoConsolesClosed.Add(handle);
        public void CloseHandle(IntPtr handle) => RawHandlesClosed.Add(handle);

        private void FailIfRequested()
        {
            if (++_stage == FailureStage)
            {
                throw new InvalidOperationException("injected native failure");
            }
        }
    }
}
