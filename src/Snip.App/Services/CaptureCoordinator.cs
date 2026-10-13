using System.Windows;
using System.Windows.Media.Imaging;
using Snip.App.ViewModels;
using Snip.App.Views;
using Snip.Infrastructure;

namespace Snip.App.Services;

/// <summary>Owns one selection session; editors can remain open independently.</summary>
public sealed class CaptureCoordinator(IDesktopCaptureService capture, AnnotationRenderer renderer, IImageExportService export) : IDisposable
{
    private bool busy, disposed;
    private SelectionSession? session;
    public async Task CaptureAsync()
    {
        if (busy || disposed) return;
        busy = true;
        var visible = Application.Current.Windows.Cast<Window>().Where(w => w.IsVisible).ToArray();
        try
        {
            foreach (var window in visible) window.Hide();
            await Task.Delay(160); // Let the compositor present the desktop before capturing.
            if (disposed) return;
            var frame = capture.Capture();
            using var active = new SelectionSession(frame, capture);
            session = active; active.Show();
            var selection = await active.Completion;
            session = null;
            if (disposed) return;
            foreach (var window in visible) window.Show();
            if (active.CancellationReason is { } reason) MessageBox.Show(reason, "自用截图", MessageBoxButton.OK, MessageBoxImage.Information);
            if (selection is not { } r) return;
            // Copy the crop so small editors do not retain the full multi-monitor desktop.
            var image = new WriteableBitmap(new CroppedBitmap(frame.Image, new Int32Rect((int)(r.X - frame.Bounds.X), (int)(r.Y - frame.Bounds.Y), (int)r.Width, (int)r.Height)));
            image.Freeze();
            var editor = new EditorWindow();
            editor.DataContext = new EditorViewModel(image, renderer, export, new EditorDialogs(() => editor));
            editor.Show(); editor.Activate();
        }
        finally
        {
            session?.Dispose(); session = null; busy = false;
            if (!disposed) foreach (var window in visible.Where(w => !w.IsVisible)) window.Show();
        }
    }
    public void Dispose() { disposed = true; session?.Dispose(); }
}
