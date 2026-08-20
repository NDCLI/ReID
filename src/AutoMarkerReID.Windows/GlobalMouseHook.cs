using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AutoMarkerReID.Windows;

public sealed class GlobalMouseHook : IDisposable
{
    private readonly HookProcedure _procedure;
    private nint _hook;
    private nint _lastBlaze;
    private nint _lastExcel;

    public GlobalMouseHook()
    {
        _procedure = HookCallback;
    }

    public event EventHandler? BlazeRightClicked;

    public void Start()
    {
        if (_hook != 0) return;
        var pointer = Marshal.GetFunctionPointerForDelegate(_procedure);
        _hook = NativeMethods.SetWindowsHookExW(NativeMethods.WhMouseLl, pointer, 0, 0);
        if (_hook == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Không thể đăng ký global mouse hook.");
    }

    public void Dispose()
    {
        if (_hook != 0)
        {
            NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = 0;
        }
        GC.KeepAlive(_procedure);
        GC.SuppressFinalize(this);
    }

    private nint HookCallback(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && lParam != 0)
        {
            var data = Marshal.PtrToStructure<MouseHookData>(lParam);
            UpdateForegroundCache();
            if (wParam.ToInt32() == NativeMethods.WmRButtonUp && _lastBlaze == NativeMethods.GetForegroundWindow() && !IsTaskbar(data.Point))
                BlazeRightClicked?.Invoke(this, EventArgs.Empty);
            else if (wParam.ToInt32() == NativeMethods.WmMButtonUp)
                ToggleBlazeAndExcel();
        }
        return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
    }

    private void UpdateForegroundCache()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        var process = ForegroundApplication.ProcessName;
        if (process?.Contains("blaze", StringComparison.OrdinalIgnoreCase) == true) _lastBlaze = foreground;
        else if (process?.Equals("EXCEL", StringComparison.OrdinalIgnoreCase) == true) _lastExcel = foreground;
    }

    private void ToggleBlazeAndExcel()
    {
        var process = ForegroundApplication.ProcessName;
        if (process?.Contains("blaze", StringComparison.OrdinalIgnoreCase) == true)
        {
            if (_lastExcel == 0) _lastExcel = FindWindow("EXCEL");
            Activate(_lastExcel);
        }
        else if (process?.Equals("EXCEL", StringComparison.OrdinalIgnoreCase) == true)
        {
            if (_lastBlaze == 0) _lastBlaze = FindWindow("blaze");
            Activate(_lastBlaze);
        }
    }

    private static nint FindWindow(string processName)
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.MainWindowHandle != 0 && process.ProcessName.Contains(processName, StringComparison.OrdinalIgnoreCase))
                    return process.MainWindowHandle;
            }
        }
        return 0;
    }

    private static void Activate(nint window)
    {
        if (window == 0) return;
        NativeMethods.ShowWindow(window, 9);
        NativeMethods.SetForegroundWindow(window);
    }

    private static bool IsTaskbar(NativePoint point)
    {
        var window = NativeMethods.WindowFromPoint(point);
        if (window == 0) return false;
        var buffer = new char[128];
        var length = NativeMethods.GetClassNameW(window, buffer, buffer.Length);
        if (length <= 0) return false;
        var className = new string(buffer, 0, length);
        return className is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "TrayNotifyWnd" or "MSTaskListWClass";
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint HookProcedure(int code, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct MouseHookData(NativePoint Point, uint MouseData, uint Flags, uint Time, nuint ExtraInfo);
}
