using System.Windows;
using System.Windows.Media.Imaging;
using Snip.App.ViewModels;
using Snip.App.Views;
using Snip.Infrastructure;
using Snip.Core;

namespace Snip.App.Services;

/// <summary>Owns one selection session; editors can remain open independently.</summary>
public sealed class CaptureCoordinator(IDesktopCaptureService capture, AnnotationRenderer renderer, IImageExportService export, FeatureHub features, DiagnosticJournal? diagnostics = null) : IDisposable
{
    private bool busy, disposed;
    private SelectionSession? session;
    public async Task CaptureAsync()
    {
        if (busy || disposed) return;
        busy = true;
        using var operation = diagnostics?.Begin("selection");
        var foreground = DesktopWindows.Foreground;
        var visible = Application.Current.Windows.Cast<Window>().Where(w => w.IsVisible).ToArray();
        try
        {
            foreach (var window in visible) window.Hide();
            await Task.Delay(160); // Let the compositor present the desktop before capturing.
            if (disposed) return;
            var frame = capture.Capture();
            operation?.Stage("desktop-captured", new { frame.Image.PixelWidth, frame.Image.PixelHeight, DisplayCount = frame.Displays.Count });
            var targets = DesktopWindows.Snapshot();
            using var active = new SelectionSession(frame, capture, renderer, export, targets);
            session = active; active.Show();
            var selection = await active.Completion;
            operation?.Stage("selection-ended", new { Action = selection?.Action.ToString(), Width = selection?.Region.Width, Height = selection?.Region.Height });
            session = null;
            if (disposed) return;
            Restore(visible);
            if (active.CancellationReason is { } reason) MessageBox.Show(reason, "自用截图", MessageBoxButton.OK, MessageBoxImage.Information);
            if (selection is not null) await features.HandleAsync(selection);
            else DesktopWindows.Activate(foreground);
            if (selection is null) operation?.Cancel("selection-cancelled"); else operation?.Complete();
        }
        catch (Exception error) { operation?.Fail(error); throw; }
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
