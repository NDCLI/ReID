using System;
using System.Runtime.InteropServices;

namespace SnipToolApp
{
    internal static class NativeMethods
    {
        [DllImport("ScreenCaptureNative.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        public static extern bool CaptureScreenToPng(int x, int y, int width, int height, out IntPtr data, out int size);

        [DllImport("ScreenCaptureNative.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void FreeCaptureData(IntPtr data);

        [DllImport("ScreenCaptureNative.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        public static extern bool StartScreenRecording([MarshalAs(UnmanagedType.LPWStr)] string outputPath);

        [DllImport("ScreenCaptureNative.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void StopScreenRecording();
    }
}
