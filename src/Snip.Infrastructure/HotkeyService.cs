using System;
using System.Windows.Interop;
using Snip.Core;

namespace Snip.Infrastructure;

public sealed class HotkeyService : IHotkeyService
{
    private const int HotkeyId = 0x5311;
    private readonly HwndSource source = new(new HwndSourceParameters("Snip.Hotkey")
    { ParentWindow = new IntPtr(-3), Width = 0, Height = 0, WindowStyle = 0 });
    private bool registered;
    public event EventHandler? Triggered;
    public HotkeyService() => source.AddHook(Hook);
    public bool Register() => registered || (registered = NativeMethods.RegisterHotKey(source.Handle, HotkeyId, 0x0001 | 0x0002 | 0x4000, 0x41));
    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0312 && wParam.ToInt32() == HotkeyId) { handled = true; Triggered?.Invoke(this, EventArgs.Empty); }
        return IntPtr.Zero;
    }
    public void Dispose()
    {
        if (registered) { NativeMethods.UnregisterHotKey(source.Handle, HotkeyId); registered = false; }
        source.RemoveHook(Hook);
        source.Dispose();
    }
}
