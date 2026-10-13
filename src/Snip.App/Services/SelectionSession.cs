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
    private readonly TaskCompletionSource<SelectionResult?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private bool closed;
    public SelectionViewModel ViewModel { get; }
    public Task<SelectionResult?> Completion => completion.Task;
    public EditorViewModel? Editor { get; private set; }
    public bool Editing { get; private set; }
    public IReadOnlyList<WindowCaptureInfo> Targets { get; }
    public event EventHandler? EditorChanged;
    private readonly AnnotationRenderer renderer;
    private readonly IImageExportService export;
    public string? CancellationReason { get; private set; }
    public SelectionSession(DesktopFrame frame, IDesktopCaptureService capture, AnnotationRenderer renderer, IImageExportService export, IReadOnlyList<WindowCaptureInfo> targets)
    {
        this.capture = capture; this.renderer = renderer; this.export = export; Targets = targets; ViewModel = new(frame);
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
    public void BeginEditing()
    {
        if (closed || !ViewModel.CanConfirm) return;
        var r = ViewModel.Selection.RoundOut();
        var image = new WriteableBitmap(new CroppedBitmap(ViewModel.Frame.Image,
            new Int32Rect((int)(r.X - ViewModel.Frame.Bounds.X), (int)(r.Y - ViewModel.Frame.Bounds.Y), (int)r.Width, (int)r.Height)));
        image.Freeze();
        var owner = windows.FirstOrDefault(w => w.IsActive) ?? windows[0];
        Editor = new EditorViewModel(image, renderer, export, new EditorDialogs(() => owner));
        Editing = true; EditorChanged?.Invoke(this, EventArgs.Empty);
    }
    public async void Confirm()
    {
        if (closed || !ViewModel.CanConfirm) return;
        if (Editor is null) { BeginEditing(); return; }
        try
        {
            if (!LayoutMatches()) { Cancel("显示器布局已变化，请重新截图。"); return; }
            if (await Editor.CopyAndMarkAsync()) Finish(null);
        }
        catch (Exception e) { MessageBox.Show(e.Message, "复制失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    public void Request(SelectionAction action, BrowserCaptureMode browserMode = BrowserCaptureMode.EntirePage)
    {
        if (Editor is null) BeginEditing();
        if (Editor is null) return;
        _ = Editor.Compose();
        Finish(new(Editor, ViewModel.Selection, action, browserMode));
    }
    public void AdjustSelection()
    {
        if (Editor?.IsDirty == true && MessageBox.Show("重新选择区域会清除当前标注，继续？", "调整选区", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        Editor = null; Editing = false; EditorChanged?.Invoke(this, EventArgs.Empty);
    }
    public void Nudge(int dx, int dy)
    {
        if (Editor?.IsDirty == true || Editor?.InlineText is not null) return;
        ViewModel.Nudge(dx, dy); if (Editing) BeginEditing();
    }
    public void AddSticker(Window owner)
    {
        if (Editor is null) return;
        var picker = new StickerPicker { Owner = owner };
        if (picker.ShowDialog() == true && picker.Png is { } png) Editor.AddSticker(png, picker.ImageWidth, picker.ImageHeight);
    }
    public void Cancel(string? reason = null) { if (closed) return; CancellationReason = reason; Finish(null); }
    private bool LayoutMatches() => capture.GetDisplays().SequenceEqual(ViewModel.Frame.Displays);
    private void CheckLayout(object? sender, EventArgs e)
    {
        try { if (!LayoutMatches()) Cancel("显示器布局已变化，请重新截图。"); }
        catch (Exception) { Cancel("无法继续读取显示器布局，请重新截图。"); }
    }
    private void Finish(SelectionResult? rect)
    {
        if (closed) return;
        closed = true; timer.Stop();
        foreach (var window in windows) window.Close();
        windows.Clear(); completion.TrySetResult(rect);
    }
    public void Dispose() => Finish(null);
}

public enum SelectionAction { Editor, Pin, Scroll, WeChat, Ocr, Translate }
public sealed record SelectionResult(EditorViewModel Editor, PixelRect Region, SelectionAction Action, BrowserCaptureMode BrowserMode = BrowserCaptureMode.EntirePage);
