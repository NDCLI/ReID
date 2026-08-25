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
    private bool _suppressBlazeRightClick;
    private bool _switchBlazeAndExcel;

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
        try
        {
            if (code >= 0 && lParam != 0)
            {
                var data = Marshal.PtrToStructure<MouseHookData>(lParam);
                var foreground = NativeMethods.GetForegroundWindow();
                var processName = GetProcessName(foreground);
                UpdateForegroundCache(foreground, processName);
                var message = wParam.ToInt32();
                if (message == NativeMethods.WmRButtonDown && _lastBlaze == foreground && !IsTaskbar(data.Point))
                {
                    _suppressBlazeRightClick = true;
                    return 1;
                }

                if (message == NativeMethods.WmRButtonUp && _suppressBlazeRightClick)
                {
                    _suppressBlazeRightClick = false;
                    BlazeRightClicked?.Invoke(this, EventArgs.Empty);
                    return 1;
                }

                if (message == NativeMethods.WmMButtonDown && IsBlazeOrExcel(processName))
                {
                    _switchBlazeAndExcel = true;
                    return 1;
                }

                if (message == NativeMethods.WmMButtonUp && _switchBlazeAndExcel)
                {
                    _switchBlazeAndExcel = false;
                    ToggleBlazeAndExcel(processName);
                    return 1;
                }
            }
        }
        catch (Exception)
        {
            // Exceptions must never escape a low-level hook callback or Windows can remove the hook.
        }
        return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
    }

    private void UpdateForegroundCache(nint foreground, string? processName)
    {
        if (processName?.Contains("blaze", StringComparison.OrdinalIgnoreCase) == true) _lastBlaze = foreground;
        else if (processName?.Equals("EXCEL", StringComparison.OrdinalIgnoreCase) == true) _lastExcel = foreground;
    }

    private void ToggleBlazeAndExcel(string? processName)
    {
        if (processName?.Contains("blaze", StringComparison.OrdinalIgnoreCase) == true)
        {
            if (!IsUsableWindow(_lastExcel)) _lastExcel = FindWindow("EXCEL");
            Activate(_lastExcel);
        }
        else if (processName?.Equals("EXCEL", StringComparison.OrdinalIgnoreCase) == true)
        {
            if (!IsUsableWindow(_lastBlaze)) _lastBlaze = FindWindow("blaze");
            Activate(_lastBlaze);
        }
    }

    private static bool IsBlazeOrExcel(string? processName) =>
        processName?.Contains("blaze", StringComparison.OrdinalIgnoreCase) == true ||
        processName?.Equals("EXCEL", StringComparison.OrdinalIgnoreCase) == true;

    private static nint FindWindow(string processName)
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.MainWindowHandle != 0 && process.ProcessName.Contains(processName, StringComparison.OrdinalIgnoreCase))
                        return process.MainWindowHandle;
                }
                catch (InvalidOperationException)
                {
                    // The process may exit while the process list is being inspected.
                }
                catch (Win32Exception)
                {
                    // Ignore protected processes that cannot be queried.
                }
            }
        }
        return 0;
    }

    private static void Activate(nint window)
    {
        if (!IsUsableWindow(window)) return;

        if (NativeMethods.IsIconic(window))
            NativeMethods.ShowWindow(window, NativeMethods.SwRestore);

        var currentThread = NativeMethods.GetCurrentThreadId();
        var foreground = NativeMethods.GetForegroundWindow();
        var foregroundThread = foreground == 0 ? 0 : NativeMethods.GetWindowThreadProcessId(foreground, out _);
        var targetThread = NativeMethods.GetWindowThreadProcessId(window, out _);
        var attachedForeground = foregroundThread != 0 && foregroundThread != currentThread &&
                                 NativeMethods.AttachThreadInput(currentThread, foregroundThread, true);
        var attachedTarget = targetThread != 0 && targetThread != currentThread && targetThread != foregroundThread &&
                             NativeMethods.AttachThreadInput(currentThread, targetThread, true);
        try
        {
            NativeMethods.BringWindowToTop(window);
            NativeMethods.SetForegroundWindow(window);
            NativeMethods.SetFocus(window);
        }
        finally
        {
            if (attachedTarget) NativeMethods.AttachThreadInput(currentThread, targetThread, false);
            if (attachedForeground) NativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
        }
    }

    private static bool IsUsableWindow(nint window) => window != 0 && NativeMethods.IsWindow(window);

    private static string? GetProcessName(nint window)
    {
        if (window == 0) return null;
        NativeMethods.GetWindowThreadProcessId(window, out var processId);
        if (processId == 0) return null;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
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
