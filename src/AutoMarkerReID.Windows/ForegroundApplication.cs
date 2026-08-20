using System.Diagnostics;

namespace AutoMarkerReID.Windows;

public static class ForegroundApplication
{
    public static string? ProcessName
    {
        get
        {
            var window = NativeMethods.GetForegroundWindow();
            if (window == 0) return null;
            NativeMethods.GetWindowThreadProcessId(window, out var processId);
            if (processId == 0) return null;
            try { return Process.GetProcessById((int)processId).ProcessName; }
            catch (ArgumentException) { return null; }
        }
    }

    public static bool IsBlazeOrExcel()
    {
        var process = ProcessName;
        return process is not null && (process.Contains("blaze", StringComparison.OrdinalIgnoreCase) ||
                                       process.Equals("EXCEL", StringComparison.OrdinalIgnoreCase));
    }
}
