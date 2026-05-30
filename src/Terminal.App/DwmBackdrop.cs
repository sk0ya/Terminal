using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Shell;

namespace Terminal;

internal static class DwmBackdrop
{
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMWA_MICA_EFFECT = 1029;

    private enum DWMSBT
    {
        DWMSBT_AUTO = 0,
        DWMSBT_NONE = 1,
        DWMSBT_MAINWINDOW = 2,
        DWMSBT_TRANSIENTWINDOW = 3,
        DWMSBT_TABBEDWINDOW = 4,
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref MARGINS pMarInset);

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int cxLeftWidth, cxRightWidth, cyTopHeight, cyBottomHeight;
    }

    internal static bool Apply(Window window, string backdropType)
    {
        if (backdropType == "none")
        {
            return false;
        }

        try
        {
            nint hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == nint.Zero)
            {
                return false;
            }

            var margins = new MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
            DwmExtendFrameIntoClientArea(hwnd, ref margins);

            DWMSBT sbt = backdropType switch
            {
                "mica" => DWMSBT.DWMSBT_MAINWINDOW,
                "acrylic" => DWMSBT.DWMSBT_TRANSIENTWINDOW,
                "mica-alt" => DWMSBT.DWMSBT_TABBEDWINDOW,
                _ => DWMSBT.DWMSBT_AUTO,
            };

            int value = (int)sbt;
            int hr = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref value, sizeof(int));

            if (hr != 0)
            {
                int micaOn = 1;
                DwmSetWindowAttribute(hwnd, DWMWA_MICA_EFFECT, ref micaOn, sizeof(int));
            }

            if (WindowChrome.GetWindowChrome(window) is WindowChrome chrome)
            {
                chrome.GlassFrameThickness = new Thickness(-1);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
