using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Snip.App.ViewModels;
using Snip.App.Views;
using Snip.Core;
using Snip.Infrastructure;

namespace Snip.App.Services;

public sealed class SelectionSession : IDisposable
{
    private readonly IDesktopCaptureService capture;
    private readonly List<SelectionWindow> windows = [];
    private readonly TaskCompletionSource<PixelRect?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private bool closed;
    public SelectionViewModel ViewModel { get; }
    public Task<PixelRect?> Completion => completion.Task;
    public string? CancellationReason { get; private set; }
    public SelectionSession(DesktopFrame frame, IDesktopCaptureService capture)
    {
        this.capture = capture; ViewModel = new(frame);
        timer.Tick += CheckLayout;
    }
    public void Show()
    {
        try
        {
            foreach (var display in ViewModel.Frame.Displays)
            {
                var window = new SelectionWindow(this, capture, display);
                windows.Add(window); window.Show(); capture.PlaceOverlay(window, display.Bounds);
            }
            var active = windows.FirstOrDefault(w => w.Display.Bounds.Contains(capture.CursorPosition)) ?? windows[0];
            active.Activate(); active.Focus(); timer.Start();
        }
        catch { Dispose(); throw; }
    }
    public void Confirm()
    {
        if (closed || !ViewModel.CanConfirm) return;
        try { if (!LayoutMatches()) { Cancel("显示器布局已变化，请重新截图。"); return; } }
        catch (Exception) { Cancel("无法继续读取显示器布局，请重新截图。"); return; }
        Finish(ViewModel.Selection.RoundOut());
    }
    public void Cancel(string? reason = null) { if (closed) return; CancellationReason = reason; Finish(null); }
    private bool LayoutMatches() => capture.GetDisplays().SequenceEqual(ViewModel.Frame.Displays);
    private void CheckLayout(object? sender, EventArgs e)
    {
        try { if (!LayoutMatches()) Cancel("显示器布局已变化，请重新截图。"); }
        catch (Exception) { Cancel("无法继续读取显示器布局，请重新截图。"); }
    }
    private void Finish(PixelRect? rect)
    {
        if (closed) return;
        closed = true; timer.Stop();
        foreach (var window in windows) window.Close();
        windows.Clear(); completion.TrySetResult(rect);
    }
    public void Dispose() => Finish(null);
}
