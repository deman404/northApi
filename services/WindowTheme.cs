using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace northApi.services;

internal static class WindowTheme
{
    public static void UseDarkTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var enabled = 1;
        // Older Windows 10 versions use attribute 19 for immersive dark mode.
        if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));

        // Windows 11 also supports explicit caption and text colors (COLORREF).
        var background = 0x00212120;
        var foreground = 0x00E6E6E6;
        DwmSetWindowAttribute(handle, 35, ref background, sizeof(int));
        DwmSetWindowAttribute(handle, 36, ref foreground, sizeof(int));
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr window, int attribute, ref int value, int size);
}
