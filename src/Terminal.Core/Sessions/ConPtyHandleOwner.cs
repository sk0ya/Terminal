using Microsoft.Win32.SafeHandles;

namespace Terminal.Sessions;

/// <summary>
/// Owns the native handles that make up a ConPTY session. Ownership can be
/// transferred to a shutdown snapshot exactly once so asynchronous shutdown
/// never races with a second disposer over the same handles.
/// </summary>
internal sealed class ConPtyHandleOwner
{
    private readonly object _syncRoot = new();
    private bool _detached;

    internal SafeFileHandle? PseudoConsoleInputRead { get; private set; }
    internal SafeFileHandle? PseudoConsoleOutputWrite { get; private set; }
    internal SafeFileHandle? InputWrite { get; private set; }
    internal SafeFileHandle? OutputRead { get; private set; }
    internal IntPtr PseudoConsole { get; private set; }
    internal IntPtr Process { get; private set; }
    internal IntPtr Thread { get; private set; }
    internal IntPtr Job { get; private set; }

    internal void SetPseudoConsole(
        IntPtr pseudoConsole,
        SafeFileHandle inputRead,
        SafeFileHandle outputWrite,
        SafeFileHandle inputWrite,
        SafeFileHandle outputRead)
    {
        lock (_syncRoot)
        {
            ThrowIfDetached();
            PseudoConsole = pseudoConsole;
            PseudoConsoleInputRead = inputRead;
            PseudoConsoleOutputWrite = outputWrite;
            InputWrite = inputWrite;
            OutputRead = outputRead;
        }
    }

    internal void AdoptPseudoConsole(
        IntPtr pseudoConsole,
        IntPtr inputRead,
        IntPtr outputWrite,
        IntPtr inputWrite,
        IntPtr outputRead,
        Func<IntPtr, SafeFileHandle> createPipeHandle,
        Action<IntPtr> closePseudoConsole,
        Action<IntPtr> closeRawHandle)
    {
        SafeFileHandle? ownedInputRead = null;
        SafeFileHandle? ownedOutputWrite = null;
        SafeFileHandle? ownedInputWrite = null;
        SafeFileHandle? ownedOutputRead = null;

        try
        {
            ownedInputRead = createPipeHandle(inputRead);
            inputRead = IntPtr.Zero;
            ownedOutputWrite = createPipeHandle(outputWrite);
            outputWrite = IntPtr.Zero;
            ownedInputWrite = createPipeHandle(inputWrite);
            inputWrite = IntPtr.Zero;
            ownedOutputRead = createPipeHandle(outputRead);
            outputRead = IntPtr.Zero;

            SetPseudoConsole(
                pseudoConsole,
                ownedInputRead,
                ownedOutputWrite,
                ownedInputWrite,
                ownedOutputRead);
            pseudoConsole = IntPtr.Zero;
            ownedInputRead = null;
            ownedOutputWrite = null;
            ownedInputWrite = null;
            ownedOutputRead = null;
        }
        finally
        {
            DisposeQuietly(ownedInputRead);
            DisposeQuietly(ownedOutputWrite);
            DisposeQuietly(ownedInputWrite);
            DisposeQuietly(ownedOutputRead);
            CloseRawOnce(inputRead, closeRawHandle);
            CloseRawOnce(outputWrite, closeRawHandle);
            CloseRawOnce(inputWrite, closeRawHandle);
            CloseRawOnce(outputRead, closeRawHandle);
            CloseRawOnce(pseudoConsole, closePseudoConsole);
        }
    }

    internal void SetProcess(IntPtr process, IntPtr thread)
    {
        lock (_syncRoot)
        {
            ThrowIfDetached();
            Process = process;
            Thread = thread;
        }
    }

    internal void SetJob(IntPtr job)
    {
        lock (_syncRoot)
        {
            ThrowIfDetached();
            Job = job;
        }
    }

    internal void ReleasePseudoConsoleEndpoints()
    {
        SafeFileHandle? inputRead;
        SafeFileHandle? outputWrite;
        lock (_syncRoot)
        {
            inputRead = PseudoConsoleInputRead;
            outputWrite = PseudoConsoleOutputWrite;
            PseudoConsoleInputRead = null;
            PseudoConsoleOutputWrite = null;
        }

        DisposeQuietly(inputRead);
        DisposeQuietly(outputWrite);
    }

    internal ConPtyOwnedHandles? DetachForShutdown()
    {
        lock (_syncRoot)
        {
            if (_detached)
            {
                return null;
            }

            _detached = true;
            var detached = new ConPtyOwnedHandles(
                PseudoConsoleInputRead,
                PseudoConsoleOutputWrite,
                InputWrite,
                OutputRead,
                PseudoConsole,
                Process,
                Thread,
                Job);

            PseudoConsoleInputRead = null;
            PseudoConsoleOutputWrite = null;
            InputWrite = null;
            OutputRead = null;
            PseudoConsole = IntPtr.Zero;
            Process = IntPtr.Zero;
            Thread = IntPtr.Zero;
            Job = IntPtr.Zero;
            return detached;
        }
    }

    private void ThrowIfDetached() =>
        ObjectDisposedException.ThrowIf(_detached, this);

    private static void DisposeQuietly(IDisposable? disposable)
    {
        try
        {
            disposable?.Dispose();
        }
        catch
        {
        }
    }

    private static void CloseRawOnce(IntPtr handle, Action<IntPtr> close)
    {
        if (handle != IntPtr.Zero)
        {
            close(handle);
        }
    }
}

internal sealed class ConPtyOwnedHandles
{
    private SafeFileHandle? _pseudoConsoleInputRead;
    private SafeFileHandle? _pseudoConsoleOutputWrite;
    private SafeFileHandle? _inputWrite;
    private SafeFileHandle? _outputRead;
    private IntPtr _pseudoConsole;
    private IntPtr _process;
    private IntPtr _thread;
    private IntPtr _job;

    internal ConPtyOwnedHandles(
        SafeFileHandle? pseudoConsoleInputRead,
        SafeFileHandle? pseudoConsoleOutputWrite,
        SafeFileHandle? inputWrite,
        SafeFileHandle? outputRead,
        IntPtr pseudoConsole,
        IntPtr process,
        IntPtr thread,
        IntPtr job)
    {
        _pseudoConsoleInputRead = pseudoConsoleInputRead;
        _pseudoConsoleOutputWrite = pseudoConsoleOutputWrite;
        _inputWrite = inputWrite;
        _outputRead = outputRead;
        _pseudoConsole = pseudoConsole;
        _process = process;
        _thread = thread;
        _job = job;
    }

    internal IntPtr Process => _process;
    internal IntPtr Job => _job;

    internal void CloseCommunicationHandles(Action<IntPtr> closePseudoConsole)
    {
        // ClosePseudoConsole blocks until the console host has flushed its pending output, so it has
        // to run before the pipes it flushes through are torn down. Closing the output read handle
        // first leaves the host writing into a pipe nobody drains, and shutdown never returns.
        IntPtr pseudoConsole = Interlocked.Exchange(ref _pseudoConsole, IntPtr.Zero);
        if (pseudoConsole != IntPtr.Zero)
        {
            closePseudoConsole(pseudoConsole);
        }

        DisposeOnce(ref _pseudoConsoleInputRead);
        DisposeOnce(ref _pseudoConsoleOutputWrite);
        DisposeOnce(ref _inputWrite);
        DisposeOnce(ref _outputRead);
    }

    internal void CloseProcessHandles(Action<IntPtr> closeHandle)
    {
        CloseOnce(ref _thread, closeHandle);
        CloseOnce(ref _process, closeHandle);
        CloseOnce(ref _job, closeHandle);
    }

    private static void DisposeOnce(ref SafeFileHandle? handle)
    {
        SafeFileHandle? detached = Interlocked.Exchange(ref handle, null);
        try
        {
            detached?.Dispose();
        }
        catch
        {
        }
    }

    private static void CloseOnce(ref IntPtr handle, Action<IntPtr> closeHandle)
    {
        IntPtr detached = Interlocked.Exchange(ref handle, IntPtr.Zero);
        if (detached != IntPtr.Zero)
        {
            closeHandle(detached);
        }
    }
}
