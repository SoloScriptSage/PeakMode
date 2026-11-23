using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PeakModeTray
{
    // Native dark title bar via DWM, so snap layouts and caption buttons
    // stay native. Every call is best effort: older Windows ignores or
    // rejects the attribute and keeps its default title bar.
    internal static class DarkTitleBar
    {
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;  // Windows 10 1809..1909
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE     = 20;  // Windows 10 2004+, 11
        private const int DWMWA_BORDER_COLOR                = 34;  // Windows 11
        private const int DWMWA_CAPTION_COLOR               = 35;  // Windows 11

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public static void Apply(Window window, uint captionRgb, uint borderRgb)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            try
            {
                int on = 1;
                if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int)) != 0)
                    DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref on, sizeof(int));
                int caption = ToColorRef(captionRgb);
                int border  = ToColorRef(borderRgb);
                DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
                DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        // 0xRRGGBB -> COLORREF 0x00BBGGRR
        private static int ToColorRef(uint rgb) =>
            (int)(((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF));
    }
}
