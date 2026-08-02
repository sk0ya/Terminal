using Microsoft.Win32.SafeHandles;
using Terminal.Sessions;

namespace Terminal.Tests;

public sealed class ConPtyHandleOwnerTests
{
    [Fact]
    public void DetachForShutdownTransfersEveryHandleExactlyOnce()
    {
        var owner = new ConPtyHandleOwner();
        using var inputRead = NonOwningHandle();
        using var outputWrite = NonOwningHandle();
        using var inputWrite = NonOwningHandle();
        using var outputRead = NonOwningHandle();
        owner.SetPseudoConsole((IntPtr)1, inputRead, outputWrite, inputWrite, outputRead);
        owner.SetProcess((IntPtr)2, (IntPtr)3);
        owner.SetJob((IntPtr)4);

        ConPtyOwnedHandles detached = Assert.IsType<ConPtyOwnedHandles>(owner.DetachForShutdown());

        Assert.Null(owner.DetachForShutdown());
        Assert.Equal(IntPtr.Zero, owner.PseudoConsole);
        Assert.Equal(IntPtr.Zero, owner.Process);
        Assert.Equal(IntPtr.Zero, owner.Thread);
        Assert.Equal(IntPtr.Zero, owner.Job);
        Assert.Null(owner.InputWrite);
        Assert.Null(owner.OutputRead);
        Assert.Equal((IntPtr)2, detached.Process);
        Assert.Equal((IntPtr)4, detached.Job);
    }

    [Fact]
    public void ShutdownSnapshotClosesCommunicationBeforeProcessHandlesAndIsIdempotent()
    {
        using var inputRead = NonOwningHandle();
        using var outputWrite = NonOwningHandle();
        using var inputWrite = NonOwningHandle();
        using var outputRead = NonOwningHandle();
        var handles = new ConPtyOwnedHandles(
            inputRead,
            outputWrite,
            inputWrite,
            outputRead,
            (IntPtr)1,
            (IntPtr)2,
            (IntPtr)3,
            (IntPtr)4);
        var closed = new List<IntPtr>();

        handles.CloseCommunicationHandles(handle => closed.Add(handle));
        handles.CloseProcessHandles(handle => closed.Add(handle));
        handles.CloseCommunicationHandles(handle => closed.Add(handle));
        handles.CloseProcessHandles(handle => closed.Add(handle));

        Assert.True(inputRead.IsClosed);
        Assert.True(outputWrite.IsClosed);
        Assert.True(inputWrite.IsClosed);
        Assert.True(outputRead.IsClosed);
        Assert.Equal([(IntPtr)1, (IntPtr)3, (IntPtr)2, (IntPtr)4], closed);
    }

    [Fact]
    public void ShutdownSnapshotClosesPseudoConsoleBeforeItsPipes()
    {
        // ClosePseudoConsole blocks until the console host has flushed through these pipes, so
        // closing them first hangs shutdown for good.
        using var inputRead = NonOwningHandle();
        using var outputWrite = NonOwningHandle();
        using var inputWrite = NonOwningHandle();
        using var outputRead = NonOwningHandle();
        var handles = new ConPtyOwnedHandles(
            inputRead,
            outputWrite,
            inputWrite,
            outputRead,
            (IntPtr)1,
            (IntPtr)2,
            (IntPtr)3,
            (IntPtr)4);
        bool pipesStillOpenWhenClosingPseudoConsole = false;

        handles.CloseCommunicationHandles(
            _ => pipesStillOpenWhenClosingPseudoConsole = !outputRead.IsClosed && !inputWrite.IsClosed);

        Assert.True(pipesStillOpenWhenClosingPseudoConsole);
        Assert.True(outputRead.IsClosed);
        Assert.True(inputWrite.IsClosed);
    }

    [Fact]
    public async Task ConcurrentDetachHasSingleWinner()
    {
        var owner = new ConPtyHandleOwner();
        using var inputRead = NonOwningHandle();
        using var outputWrite = NonOwningHandle();
        using var inputWrite = NonOwningHandle();
        using var outputRead = NonOwningHandle();
        owner.SetPseudoConsole((IntPtr)1, inputRead, outputWrite, inputWrite, outputRead);

        ConPtyOwnedHandles?[] results = await Task.WhenAll(
            Enumerable.Range(0, 16).Select(_ => Task.Run(owner.DetachForShutdown)));

        Assert.Single(results, static result => result is not null);
    }

    [Fact]
    public void ReleasedPseudoConsoleEndpointsAreNotReturnedDuringShutdown()
    {
        var owner = new ConPtyHandleOwner();
        using var inputRead = NonOwningHandle();
        using var outputWrite = NonOwningHandle();
        using var inputWrite = NonOwningHandle();
        using var outputRead = NonOwningHandle();
        owner.SetPseudoConsole((IntPtr)1, inputRead, outputWrite, inputWrite, outputRead);

        owner.ReleasePseudoConsoleEndpoints();
        ConPtyOwnedHandles detached = Assert.IsType<ConPtyOwnedHandles>(owner.DetachForShutdown());
        detached.CloseCommunicationHandles(_ => { });

        Assert.True(inputRead.IsClosed);
        Assert.True(outputWrite.IsClosed);
        Assert.True(inputWrite.IsClosed);
        Assert.True(outputRead.IsClosed);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void FailedPseudoConsoleAdoptionRecoversPseudoConsoleAndEveryPipe(int failingPipe)
    {
        var owner = new ConPtyHandleOwner();
        var adopted = new List<(IntPtr Raw, SafeFileHandle Handle)>();
        var rawClosed = new List<IntPtr>();
        var pseudoClosed = new List<IntPtr>();
        int attempt = 0;

        Assert.Throws<InvalidOperationException>(() => owner.AdoptPseudoConsole(
            (IntPtr)10,
            (IntPtr)11,
            (IntPtr)12,
            (IntPtr)13,
            (IntPtr)14,
            raw =>
            {
                if (++attempt == failingPipe)
                {
                    throw new InvalidOperationException("injected adoption failure");
                }

                var handle = new SafeFileHandle(raw, ownsHandle: false);
                adopted.Add((raw, handle));
                return handle;
            },
            pseudoClosed.Add,
            rawClosed.Add));

        Assert.Equal([(IntPtr)10], pseudoClosed);
        Assert.All(adopted, static item => Assert.True(item.Handle.IsClosed));
        IntPtr[] recoveredPipes = adopted.Select(static item => item.Raw)
            .Concat(rawClosed)
            .Order()
            .ToArray();
        Assert.Equal([(IntPtr)11, (IntPtr)12, (IntPtr)13, (IntPtr)14], recoveredPipes);
        Assert.Equal(recoveredPipes.Length, recoveredPipes.Distinct().Count());
        Assert.Equal(IntPtr.Zero, owner.PseudoConsole);
        Assert.Null(owner.InputWrite);
        Assert.Null(owner.OutputRead);
    }

    [Fact]
    public void FailedOwnerRegistrationRecoversAdoptedPseudoConsoleAndEveryPipe()
    {
        var owner = new ConPtyHandleOwner();
        Assert.NotNull(owner.DetachForShutdown());
        var adopted = new List<(IntPtr Raw, SafeFileHandle Handle)>();
        var rawClosed = new List<IntPtr>();
        var pseudoClosed = new List<IntPtr>();

        Assert.Throws<ObjectDisposedException>(() => owner.AdoptPseudoConsole(
            (IntPtr)10,
            (IntPtr)11,
            (IntPtr)12,
            (IntPtr)13,
            (IntPtr)14,
            raw =>
            {
                var handle = new SafeFileHandle(raw, ownsHandle: false);
                adopted.Add((raw, handle));
                return handle;
            },
            pseudoClosed.Add,
            rawClosed.Add));

        Assert.Equal([(IntPtr)10], pseudoClosed);
        Assert.Empty(rawClosed);
        Assert.Equal([(IntPtr)11, (IntPtr)12, (IntPtr)13, (IntPtr)14],
            adopted.Select(static item => item.Raw).ToArray());
        Assert.All(adopted, static item => Assert.True(item.Handle.IsClosed));
        Assert.Equal(IntPtr.Zero, owner.PseudoConsole);
        Assert.Null(owner.InputWrite);
        Assert.Null(owner.OutputRead);
    }

    private static SafeFileHandle NonOwningHandle() => new(IntPtr.Zero, ownsHandle: false);
}
