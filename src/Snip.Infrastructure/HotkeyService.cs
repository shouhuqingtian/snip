using System;
using System.Windows.Interop;
using Snip.Core;

namespace Snip.Infrastructure;

public sealed class HotkeyService : IHotkeyService
{
    private int activeId = 0x5311;
    private readonly HwndSource source = new(new HwndSourceParameters("Snip.Hotkey")
    { ParentWindow = new IntPtr(-3), Width = 0, Height = 0, WindowStyle = 0 });
    private bool registered;
    public HotkeyGesture? Current { get; private set; }
    public event EventHandler? Triggered;
    public HotkeyService() => source.AddHook(Hook);
    public bool Register() => Register(Current ?? HotkeyGesture.Default);
    public bool Register(HotkeyGesture gesture)
    {
        gesture.Validate();
        if (registered && Current == gesture) return true;
        int candidate = activeId == 0x5311 ? 0x5312 : 0x5311;
        // Reserve the new key first. A conflict must leave the working registration intact.
        if (!NativeMethods.RegisterHotKey(source.Handle, candidate, gesture.Modifiers | 0x4000, gesture.VirtualKey)) return false;
        if (registered) NativeMethods.UnregisterHotKey(source.Handle, activeId);
        activeId = candidate; Current = gesture; registered = true; return true;
    }
    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (registered && message == 0x0312 && wParam.ToInt32() == activeId) { handled = true; Triggered?.Invoke(this, EventArgs.Empty); }
        return IntPtr.Zero;
    }
    public void Dispose()
    {
        if (registered) { NativeMethods.UnregisterHotKey(source.Handle, activeId); registered = false; Current = null; }
        source.RemoveHook(Hook);
        source.Dispose();
    }
}
