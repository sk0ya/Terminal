using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Terminal.Sessions;

internal sealed class ConPtyPseudoConsoleFactory
{
    private readonly IConPtyPseudoConsoleApi _api;

    internal ConPtyPseudoConsoleFactory(IConPtyPseudoConsoleApi api) => _api = api;

    internal void Create(short columns, short rows, ConPtyHandleOwner owner)
    {
        IntPtr inputRead = IntPtr.Zero;
        IntPtr inputWrite = IntPtr.Zero;
        IntPtr outputRead = IntPtr.Zero;
        IntPtr outputWrite = IntPtr.Zero;
        IntPtr pseudoConsole = IntPtr.Zero;

        try
        {
            (inputRead, inputWrite) = _api.CreatePipe("input");
            (outputRead, outputWrite) = _api.CreatePipe("output");
            _api.DisableInheritance(inputWrite, "input");
            _api.DisableInheritance(outputRead, "output");
            pseudoConsole = _api.CreatePseudoConsole(columns, rows, inputRead, outputWrite);

            IntPtr adoptedPseudoConsole = pseudoConsole;
            IntPtr adoptedInputRead = inputRead;
            IntPtr adoptedOutputWrite = outputWrite;
            IntPtr adoptedInputWrite = inputWrite;
            IntPtr adoptedOutputRead = outputRead;
            Func<IntPtr, SafeFileHandle> createOwnedPipeHandle = _api.CreateOwnedPipeHandle;
            Action<IntPtr> closePseudoConsole = _api.ClosePseudoConsole;
            Action<IntPtr> closeHandle = _api.CloseHandle;
            pseudoConsole = IntPtr.Zero;
            inputRead = IntPtr.Zero;
            inputWrite = IntPtr.Zero;
            outputRead = IntPtr.Zero;
            outputWrite = IntPtr.Zero;

            owner.AdoptPseudoConsole(
                adoptedPseudoConsole,
                adoptedInputRead,
                adoptedOutputWrite,
                adoptedInputWrite,
                adoptedOutputRead,
                createOwnedPipeHandle,
                closePseudoConsole,
                closeHandle);

        }
        finally
        {
            CloseIfPresent(inputRead);
            CloseIfPresent(inputWrite);
            CloseIfPresent(outputRead);
            CloseIfPresent(outputWrite);
            if (pseudoConsole != IntPtr.Zero)
            {
                _api.ClosePseudoConsole(pseudoConsole);
            }
        }
    }

    private void CloseIfPresent(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
        {
            _api.CloseHandle(handle);
        }
    }
}

internal interface IConPtyPseudoConsoleApi
{
    (IntPtr Read, IntPtr Write) CreatePipe(string direction);
    void DisableInheritance(IntPtr handle, string direction);
    IntPtr CreatePseudoConsole(short columns, short rows, IntPtr inputRead, IntPtr outputWrite);
    SafeFileHandle CreateOwnedPipeHandle(IntPtr handle);
    void ClosePseudoConsole(IntPtr handle);
    void CloseHandle(IntPtr handle);
}

internal sealed class WindowsConPtyPseudoConsoleApi : IConPtyPseudoConsoleApi
{
    private const uint HandleFlagInherit = 0x00000001;

    internal static WindowsConPtyPseudoConsoleApi Instance { get; } = new();

    private WindowsConPtyPseudoConsoleApi() { }

    public (IntPtr Read, IntPtr Write) CreatePipe(string direction)
    {
        var security = new SecurityAttributes
        {
            Length = Marshal.SizeOf<SecurityAttributes>(),
            InheritHandle = true
        };
        if (!CreatePipeNative(out IntPtr read, out IntPtr write, ref security, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Failed to create ConPTY {direction} pipe.");
        }
        return (read, write);
    }

    public void DisableInheritance(IntPtr handle, string direction)
    {
        if (!SetHandleInformation(handle, HandleFlagInherit, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Failed to configure ConPTY {direction} pipe.");
        }
    }

    public IntPtr CreatePseudoConsole(short columns, short rows, IntPtr inputRead, IntPtr outputWrite)
    {
        int hr = CreatePseudoConsoleNative(new Coord(columns, rows), inputRead, outputWrite, 0, out IntPtr result);
        if (hr != 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }
        return result;
    }

    public SafeFileHandle CreateOwnedPipeHandle(IntPtr handle) => new(handle, ownsHandle: true);
    public void ClosePseudoConsole(IntPtr handle) => ClosePseudoConsoleNative(handle);
    public void CloseHandle(IntPtr handle) => _ = CloseHandleNative(handle);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "CreatePipe")]
    private static extern bool CreatePipeNative(out IntPtr read, out IntPtr write, ref SecurityAttributes attributes, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "CreatePseudoConsole")]
    private static extern int CreatePseudoConsoleNative(Coord size, IntPtr input, IntPtr output, uint flags, out IntPtr pseudoConsole);

    [DllImport("kernel32.dll", SetLastError = false, EntryPoint = "ClosePseudoConsole")]
    private static extern void ClosePseudoConsoleNative(IntPtr pseudoConsole);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "CloseHandle")]
    private static extern bool CloseHandleNative(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Coord
    {
        internal Coord(short x, short y) { X = x; Y = y; }
        internal readonly short X;
        internal readonly short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        internal int Length;
        internal IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] internal bool InheritHandle;
    }
}
