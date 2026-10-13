using System.Windows;
using System.Windows.Media.Imaging;
using Snip.App.ViewModels;
using Snip.App.Views;
using Snip.Infrastructure;

namespace Snip.App.Services;

/// <summary>Owns one selection session; editors can remain open independently.</summary>
public sealed class CaptureCoordinator(IDesktopCaptureService capture, AnnotationRenderer renderer, IImageExportService export, FeatureHub features) : IDisposable
{
    private bool busy, disposed;
    private SelectionSession? session;
    public async Task CaptureAsync()
    {
        if (busy || disposed) return;
        busy = true;
        var foreground = DesktopWindows.Foreground;
        var visible = Application.Current.Windows.Cast<Window>().Where(w => w.IsVisible).ToArray();
        try
        {
            foreach (var window in visible) window.Hide();
            await Task.Delay(160); // Let the compositor present the desktop before capturing.
            if (disposed) return;
            var frame = capture.Capture();
            var targets = DesktopWindows.Snapshot();
            using var active = new SelectionSession(frame, capture, renderer, export, targets) { RecognizeRequested = features.ShowText };
            session = active; active.Show();
            var selection = await active.Completion;
            session = null;
            if (disposed) return;
            Restore(visible);
            if (active.CancellationReason is { } reason) MessageBox.Show(reason, "自用截图", MessageBoxButton.OK, MessageBoxImage.Information);
            if (selection is not null) await features.HandleAsync(selection);
            else DesktopWindows.Activate(foreground);
        }
        finally
        {
            session?.Dispose(); session = null; busy = false;
            if (!disposed) Restore(visible);
        }
    }
    private static void Restore(IEnumerable<Window> windows)
    {
        var alive = Application.Current.Windows.Cast<Window>().ToHashSet();
        foreach (var window in windows) if (alive.Contains(window) && !window.IsVisible) window.Show();
    }
    public void Dispose() { disposed = true; session?.Dispose(); }
}
