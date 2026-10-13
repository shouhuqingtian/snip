using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls;
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
    private TextBox? textBox;
    private readonly VisualCollection children;
    private Guid? editingId;
    private bool changingText;
    public PixelRect? VisibleImageBounds { get; set; }
    public EditorCanvas()
    {
        children = new VisualCollection(this);
        Focusable = true; ClipToBounds = true;
        DataContextChanged += (_, _) =>
        {
            if (vm is not null) { vm.VisualChanged -= OnChanged; vm.CommitTextRequested -= CommitText; }
            vm = DataContext as EditorViewModel;
            if (vm is not null) { Width = vm.PixelWidth; Height = vm.PixelHeight; vm.UseInlineText = true; vm.VisualChanged += OnChanged; vm.CommitTextRequested += CommitText; }
            renderedRevision = -1; cached = null; InvalidateVisual();
        };
        Unloaded += (_, _) => { if (vm is not null) vm.CancelGesture(); };
    }
    protected override int VisualChildrenCount => children.Count;
    protected override Visual GetVisualChild(int index) => children[index];
    private void OnChanged(object? sender, EventArgs args) { SyncText(); InvalidateVisual(); }
    private void SyncText()
    {
        if (changingText) return;
        changingText = true;
        try
        {
            var inline = vm?.InlineText;
            bool visible = inline is not null && (VisibleImageBounds is not { } host ||
                inline.Start.X >= host.X && inline.Start.X < host.Right && inline.Start.Y >= host.Y && inline.Start.Y < host.Bottom);
            if (!visible)
            {
                if (textBox is not null) { children.Remove(textBox); RemoveLogicalChild(textBox); textBox = null; editingId = null; }
            }
            else if (inline is { } a && editingId != a.Id)
            {
                if (textBox is not null) { children.Remove(textBox); RemoveLogicalChild(textBox); }
                editingId = a.Id;
                textBox = new TextBox { Text = a.Text, FontSize = a.FontSize, FontFamily = new FontFamily("Microsoft YaHei UI"),
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(a.Color)), Background = new SolidColorBrush(Color.FromArgb(210, 255, 255, 255)),
                    AcceptsReturn = true, Padding = new Thickness(2), BorderBrush = Brushes.SeaGreen, BorderThickness = new Thickness(1), MinWidth = 100,
                    ToolTip = "Ctrl+Enter 提交；Esc 取消；点击其他位置提交" };
                textBox.PreviewKeyDown += (_, e) =>
                {
                    if (e.Key == Key.Escape) { vm?.CancelCurrent(); e.Handled = true; Focus(); }
                    else if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { CommitText(this, EventArgs.Empty); e.Handled = true; Focus(); }
                };
                textBox.LostKeyboardFocus += (_, _) => { if (!changingText) CommitText(this, EventArgs.Empty); };
                AddLogicalChild(textBox); children.Add(textBox);
                Dispatcher.BeginInvoke(() => { textBox?.Focus(); if (textBox is not null) textBox.CaretIndex = textBox.Text.Length; });
            }
            InvalidateMeasure(); InvalidateArrange();
        }
        finally { changingText = false; }
    }
    private void CommitText(object? sender, EventArgs e) { if (textBox is not null) vm?.CommitInlineText(textBox.Text); }
    protected override Size MeasureOverride(Size availableSize)
    {
        textBox?.Measure(new Size(Math.Max(100, Width - (vm?.InlineText?.Start.X ?? 0)), Math.Max(60, Height)));
        return new Size(Width, Height);
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        if (textBox is not null && vm?.InlineText is { } a)
            textBox.Arrange(new Rect(a.Start.X, a.Start.Y, Math.Min(Math.Max(100, textBox.DesiredSize.Width), Math.Max(1, Width - a.Start.X)), Math.Max(40, textBox.DesiredSize.Height)));
        return finalSize;
    }
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
            if (vm.Tool == AnnotationTool.Select)
                foreach (double x in new[] { r.X, r.X + r.Width / 2, r.Right }) foreach (double y in new[] { r.Y, r.Y + r.Height / 2, r.Bottom })
                {
                    if (x == r.X + r.Width / 2 && y == r.Y + r.Height / 2) continue;
                    double size = 6 / vm.Zoom; context.DrawRectangle(Brushes.White, pen, new Rect(x - size / 2, y - size / 2, size, size));
                }
        }
    }
    private PixelPoint Position(MouseEventArgs e) { var p = e.GetPosition(this); return new(p.X, p.Y); }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        CommitText(this, EventArgs.Empty); Focus();
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
