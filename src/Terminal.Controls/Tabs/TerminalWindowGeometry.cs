using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Terminal.Tabs;

/// <summary>
/// Window and monitor rectangles in device pixels, straight from Win32. WPF only offers DIPs, and
/// <see cref="SystemParameters.PrimaryScreenWidth"/> describes the primary monitor whichever monitor
/// the window is on, so XTWINOPS reports take their geometry from here.
/// </summary>
internal static class TerminalWindowGeometry
{
    private const uint MonitorDefaultToNearest = 2;

    /// <summary>The window's outer rectangle (frame included), in device pixels.</summary>
    public static bool TryGetWindowRect(Window window, out Int32Rect rect)
    {
        rect = default;
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out NativeRect native))
        {
            return false;
        }

        rect = native.ToInt32Rect();
        return true;
    }

    /// <summary>The bounds of the monitor the window is (mostly) on, in device pixels.</summary>
    public static bool TryGetMonitorRect(Window window, out Int32Rect rect)
    {
        rect = default;
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        IntPtr monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        rect = info.Monitor.ToInt32Rect();
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly Int32Rect ToInt32Rect() =>
            new(Left, Top, Math.Max(0, Right - Left), Math.Max(0, Bottom - Top));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
