using System.Windows;
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
        try
        {
            await Task.Delay(160); // Let the launcher click/tray menu settle before freezing the visible desktop.
            if (disposed) return;
            // The helper releases overlays and the full desktop before any optional long-running workflow.
            var (selection, cancellationReason) = await SelectAsync(operation);
            operation?.Stage("selection-ended", new { Action = selection?.Action.ToString(), Width = selection?.Region.Width, Height = selection?.Region.Height });
            if (disposed) return;
            if (cancellationReason is { } reason) MessageBox.Show(reason, "自用截图", MessageBoxButton.OK, MessageBoxImage.Information);
            if (selection is not null) await features.HandleAsync(selection);
            else DesktopWindows.Activate(foreground);
            if (selection is null) operation?.Cancel("selection-cancelled"); else operation?.Complete();
        }
        catch (Exception error) { operation?.Fail(error); throw; }
        finally
        {
            session?.Dispose(); session = null; busy = false;
        }
    }
    private async Task<(SelectionResult? Selection, string? Reason)> SelectAsync(DiagnosticOperation? operation)
    {
        var frame = await Task.Run(capture.Capture);
        if (disposed) return (null, null);
        operation?.Stage("desktop-captured", new { frame.Image.PixelWidth, frame.Image.PixelHeight, DisplayCount = frame.Displays.Count });
        using var active = new SelectionSession(frame, capture, renderer, export, DesktopWindows.Snapshot(includeCurrentProcess: true),
            () => new HoverRegionProbe(FeatureHub.WorkerHost, diagnostics));
        session = active;
        try
        {
            active.Show();
            var selection = await active.Completion;
            return (selection, active.CancellationReason);
        }
        finally { session = null; }
    }
    public void Dispose() { disposed = true; session?.Dispose(); }
}
