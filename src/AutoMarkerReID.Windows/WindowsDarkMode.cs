using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AutoMarkerReID.Windows;

public static class WindowsDarkMode
{
    public static void Apply(Window window)
    {
        void ApplyToHandle()
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == 0) return;
            var enabled = 1;
            var result = NativeMethods.DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
            if (result != 0) NativeMethods.DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
        }

        if (new WindowInteropHelper(window).Handle != 0) ApplyToHandle();
        else window.SourceInitialized += (_, _) => ApplyToHandle();
    }
}
