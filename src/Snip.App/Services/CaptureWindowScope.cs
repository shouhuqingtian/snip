using System.Windows;

namespace Snip.App.Services;

/// <summary>Keep our own windows, including topmost pins, out of desktop capture.</summary>
public sealed class CaptureWindowScope : IDisposable
{
    private readonly Window[] visible;
    public CaptureWindowScope(IEnumerable<Window> windows)
    {
        visible = windows.Where(w => w.IsVisible).ToArray();
        foreach (var window in visible) window.Hide();
    }
    public void Dispose()
    {
        foreach (var window in visible)
        {
            if (window.Dispatcher.HasShutdownStarted) continue;
            // A closed WPF window cannot be shown again; avoid restoring one closed during capture.
            try { if (!window.IsVisible && new System.Windows.Interop.WindowInteropHelper(window).Handle != IntPtr.Zero) window.Show(); }
            catch (InvalidOperationException) { }
        }
    }
}
