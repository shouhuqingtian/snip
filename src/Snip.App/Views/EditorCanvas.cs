using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snip.App.ViewModels;
using Snip.Core;
using Snip.Infrastructure;

namespace Snip.App.Views;

public sealed class EditorCanvas : FrameworkElement
{
    private readonly AnnotationRenderer renderer = new();
    private EditorViewModel? vm;
    private BitmapSource? cached;
    private int renderedRevision = -1;
    public EditorCanvas()
    {
        Focusable = true; ClipToBounds = true;
        DataContextChanged += (_, _) =>
        {
            if (vm is not null) vm.VisualChanged -= OnChanged;
            vm = DataContext as EditorViewModel;
            if (vm is not null) { Width = vm.PixelWidth; Height = vm.PixelHeight; vm.VisualChanged += OnChanged; }
            renderedRevision = -1; cached = null; InvalidateVisual();
        };
        Unloaded += (_, _) => { if (vm is not null) vm.CancelGesture(); };
    }
    private void OnChanged(object? sender, EventArgs args) => InvalidateVisual();
    protected override void OnRender(DrawingContext context)
    {
        if (vm is null) return;
        if (cached is null || renderedRevision != vm.BaseRevision)
        {
            cached = renderer.Render(vm.Image, vm.BaseAnnotations); renderedRevision = vm.BaseRevision;
        }
        context.DrawImage(cached, new Rect(0, 0, Width, Height));
        if (vm.Preview is not null) renderer.DrawAnnotation(context, vm.Preview);
        if (vm.Selected is { } selected)
        {
            var r = renderer.GetBounds(selected);
            var pen = new System.Windows.Media.Pen(Brushes.DodgerBlue, 1 / vm.Zoom) { DashStyle = DashStyles.Dash };
            context.DrawRectangle(null, pen, new Rect(r.X - 4, r.Y - 4, Math.Max(1, r.Width) + 8, Math.Max(1, r.Height) + 8));
        }
    }
    private PixelPoint Position(MouseEventArgs e) { var p = e.GetPosition(this); return new(p.X, p.Y); }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        if (vm?.BeginPointer(Position(e), e.ClickCount > 1) == true) CaptureMouse();
        e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    { if (IsMouseCaptured) { vm?.MovePointer(Position(e)); e.Handled = true; } }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!IsMouseCaptured) return;
        vm?.EndPointer(Position(e)); ReleaseMouseCapture(); e.Handled = true;
    }
    protected override void OnLostMouseCapture(MouseEventArgs e) { vm?.CancelGesture(); base.OnLostMouseCapture(e); }
    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e) { vm?.CancelGesture(); if (IsMouseCaptured) ReleaseMouseCapture(); e.Handled = true; }
}
