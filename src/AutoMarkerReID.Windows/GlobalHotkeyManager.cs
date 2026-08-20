using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;

namespace AutoMarkerReID.Windows;

[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    NoRepeat = 0x4000,
}

public sealed record HotkeyBinding(string Name, HotkeyModifiers Modifiers, Key Key)
{
    public string Gesture => $"{FormatModifiers(Modifiers)}{Key}";

    private static string FormatModifiers(HotkeyModifiers modifiers)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        return parts.Count == 0 ? string.Empty : string.Join('+', parts) + "+";
    }
}

public sealed record HotkeyRegistration(HotkeyBinding Binding, bool Registered, int? ErrorCode = null);

public sealed class GlobalHotkeyManager : IDisposable
{
    private readonly Dictionary<int, HotkeyBinding> _bindings = [];
    private HwndSource? _source;
    private nint _handle;
    private int _nextId = 0xA000;

    public event EventHandler<HotkeyBinding>? Pressed;

    public void Attach(Window window)
    {
        if (_source is not null) return;
        _handle = new WindowInteropHelper(window).Handle;
        if (_handle == 0) throw new InvalidOperationException("Cửa sổ chưa có HWND.");
        _source = HwndSource.FromHwnd(_handle) ?? throw new InvalidOperationException("Không lấy được HwndSource.");
        _source.AddHook(WindowProcedure);
    }

    public HotkeyRegistration Register(HotkeyBinding binding)
    {
        if (_source is null) throw new InvalidOperationException("Phải Attach cửa sổ trước khi đăng ký hotkey.");
        var id = _nextId++;
        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(binding.Key);
        var registered = NativeMethods.RegisterHotKey(_handle, id, (uint)(binding.Modifiers | HotkeyModifiers.NoRepeat), virtualKey);
        if (registered)
        {
            _bindings[id] = binding;
            return new HotkeyRegistration(binding, true);
        }

        return new HotkeyRegistration(binding, false, new Win32Exception().NativeErrorCode);
    }

    public void Dispose()
    {
        foreach (var id in _bindings.Keys)
        {
            NativeMethods.UnregisterHotKey(_handle, id);
        }
        _bindings.Clear();
        if (_source is not null)
        {
            _source.RemoveHook(WindowProcedure);
            _source = null;
        }
        GC.SuppressFinalize(this);
    }

    private nint WindowProcedure(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == NativeMethods.WmHotkey && _bindings.TryGetValue(wParam.ToInt32(), out var binding))
        {
            handled = true;
            Pressed?.Invoke(this, binding);
        }
        return 0;
    }
}
