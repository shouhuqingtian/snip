using System;
using System.Collections.Generic;
using System.Windows.Interop;
using Snip.Core;
namespace Snip.Infrastructure;

/// <summary>Three independent optional keys; registration changes run in one dispatcher turn with rollback.</summary>
public sealed class PinHotkeyService : IPinHotkeyService
{
    private readonly HwndSource source = new(new HwndSourceParameters("Snip.PinHotkeys") { ParentWindow = new IntPtr(-3), Width = 0, Height = 0, WindowStyle = 0 });
    private readonly HashSet<int> registered = new();
    private bool disposed;
    public PinHotkeySettings Current { get; private set; } = new();
    public event EventHandler<PinHotkeyAction>? Triggered;
    public PinHotkeyService() => source.AddHook(Hook);
    public bool Apply(PinHotkeySettings settings)
    {
        ObjectDisposedException.ThrowIf(disposed, this); settings.Validate();
        if (Current == settings) return true;
        var previous = Current;
        Clear();
        if (Register(settings)) { Current = settings; return true; }
        Clear();
        if (!Register(previous)) { Clear(); Current = new(); throw new InvalidOperationException("贴图快捷键恢复失败，请重新设置。"); }
        return false;
    }
    private bool Register(PinHotkeySettings settings)
    {
        foreach (var action in Enum.GetValues<PinHotkeyAction>())
        {
            if (settings.For(action) is not { } key) continue;
            int id = 0x5340 + (int)action;
            if (!NativeMethods.RegisterHotKey(source.Handle, id, key.Modifiers | 0x4000, key.VirtualKey)) return false;
            registered.Add(id);
        }
        return true;
    }
    private void Clear()
    { foreach (int id in registered) NativeMethods.UnregisterHotKey(source.Handle, id); registered.Clear(); }
    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        int id = wParam.ToInt32();
        if (message == 0x0312 && registered.Contains(id)) { handled = true; Triggered?.Invoke(this, (PinHotkeyAction)(id - 0x5340)); }
        return IntPtr.Zero;
    }
    public void Dispose() { if (disposed) return; disposed = true; Clear(); source.RemoveHook(Hook); source.Dispose(); }
}
