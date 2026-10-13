using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snip.App.Services;
using Snip.App.ViewModels;
using Snip.Core;
using Snip.Infrastructure;

namespace Snip.App.Views;

public sealed class SelectionWindow : Window
{
    private readonly SelectionSession session;
    public DisplayInfo Display { get; }
    public SelectionWindow(SelectionSession session, IDesktopCaptureService capture, DisplayInfo display)
    {
        this.session = session; Display = display;
        DataContext = session.ViewModel;
        Title = "自用截图 · 选择区域"; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; Topmost = true; Background = Brushes.Black;
        Width = display.Bounds.Width; Height = display.Bounds.Height;
        var root = new Grid();
        root.Children.Add(new SelectionSurface(session.ViewModel, capture, display));
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal };
        var label = new TextBlock { Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 15, 0) };
        label.SetBinding(TextBlock.TextProperty, new Binding(nameof(SelectionViewModel.SizeText))); toolbar.Children.Add(label);
        var confirm = new Button { Content = "编辑  Enter", ToolTip = "确认选区并打开编辑窗口" };
        confirm.SetBinding(IsEnabledProperty, new Binding(nameof(SelectionViewModel.CanConfirm)));
        confirm.Click += (_, _) => session.Confirm(); toolbar.Children.Add(confirm);
        var cancel = new Button { Content = "取消  Esc" }; cancel.Click += (_, _) => session.Cancel(); toolbar.Children.Add(cancel);
        var toolbarBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(245, 27, 40, 55)), CornerRadius = new CornerRadius(8), Padding = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(16), Child = toolbar
        };
        root.Children.Add(toolbarBorder);
        var hint = new TextBlock { Text = "拖动选择 · 拖动边框调整 · 拖动内部移动 · 右键取消", Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromArgb(190, 27, 40, 55)), Padding = new Thickness(12),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(16), IsHitTestVisible = false };
        root.Children.Add(hint); Content = root;
        SourceInitialized += (_, _) => capture.PlaceOverlay(this, display.Bounds);
        PreviewKeyDown += OnKey;
        MouseRightButtonDown += (_, e) => { session.Cancel(); e.Handled = true; };
        Closed += (_, _) => session.Cancel();
    }
    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { session.Cancel(); e.Handled = true; }
        else if (e.Key == Key.Enter) { session.Confirm(); e.Handled = true; }
    }
}

internal sealed class SelectionSurface : FrameworkElement
{
    private readonly SelectionViewModel vm;
    private readonly IDesktopCaptureService capture;
    private readonly DisplayInfo display;
    private readonly BitmapSource image;
    public SelectionSurface(SelectionViewModel vm, IDesktopCaptureService capture, DisplayInfo display)
    {
        this.vm = vm; this.capture = capture; this.display = display; ClipToBounds = true; Focusable = true;
        var bounds = display.Bounds; var desktop = vm.Frame.Bounds;
        image = new CroppedBitmap(vm.Frame.Image, new Int32Rect((int)(bounds.X - desktop.X), (int)(bounds.Y - desktop.Y), (int)bounds.Width, (int)bounds.Height));
        image.Freeze();
        vm.Changed += OnChanged;
        Unloaded += (_, _) => { vm.Changed -= OnChanged; if (IsMouseCaptured) ReleaseMouseCapture(); };
    }
    private void OnChanged(object? sender, EventArgs e) => InvalidateVisual();
    protected override void OnRender(DrawingContext context)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        context.DrawImage(image, bounds);
        double scaleX = ActualWidth / display.Bounds.Width, scaleY = ActualHeight / display.Bounds.Height;
        var r = vm.Selection;
        var local = new Rect((r.X - display.Bounds.X) * scaleX, (r.Y - display.Bounds.Y) * scaleY, r.Width * scaleX, r.Height * scaleY);
        var shade = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(bounds), new RectangleGeometry(local));
        context.DrawGeometry(new SolidColorBrush(Color.FromArgb(135, 0, 0, 0)), null, shade);
        if (r.IsEmpty) return;
        context.DrawRectangle(null, new System.Windows.Media.Pen(Brushes.SpringGreen, 1.5), local);
        double[] xs = [local.Left, (local.Left + local.Right) / 2, local.Right], ys = [local.Top, (local.Top + local.Bottom) / 2, local.Bottom];
        foreach (var x in xs) foreach (var y in ys)
        {
            if (x == xs[1] && y == ys[1]) continue;
            context.DrawRectangle(Brushes.White, new System.Windows.Media.Pen(Brushes.SeaGreen, 1), new Rect(x - 3, y - 3, 6, 6));
        }
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    { Focus(); vm.Begin(capture.CursorPosition); CaptureMouse(); e.Handled = true; }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        var point = capture.CursorPosition;
        if (IsMouseCaptured) vm.Move(point);
        else Cursor = SelectionGeometry.HitTest(vm.Selection, point) switch
        {
            SelectionHandle.Move => Cursors.SizeAll,
            SelectionHandle.Left or SelectionHandle.Right => Cursors.SizeWE,
            SelectionHandle.Top or SelectionHandle.Bottom => Cursors.SizeNS,
            SelectionHandle.TopLeft or SelectionHandle.BottomRight => Cursors.SizeNWSE,
            SelectionHandle.TopRight or SelectionHandle.BottomLeft => Cursors.SizeNESW,
            _ => Cursors.Cross
        };
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    { if (!IsMouseCaptured) return; vm.End(capture.CursorPosition); ReleaseMouseCapture(); e.Handled = true; }
    protected override void OnLostMouseCapture(MouseEventArgs e) { vm.CancelDrag(); base.OnLostMouseCapture(e); }
}
