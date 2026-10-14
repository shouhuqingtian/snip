using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Snip.Infrastructure;

/// <summary>Use desktop pixels for synchronous native calls, restoring the calling thread afterwards.
/// Never keep this scope across an await: DPI awareness belongs to an OS thread.</summary>
internal sealed class PhysicalDesktopScope : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    private readonly IntPtr previous;
    private bool disposed;
    public PhysicalDesktopScope()
    {
        previous = SetThreadDpiAwarenessContext(new IntPtr(-4)); // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
        if (previous == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        SetThreadDpiAwarenessContext(previous);
    }
}
