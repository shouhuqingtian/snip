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
    private DrawingGroup? cached;
    private bool subscribed;
    private int renderedRevision = -1;
    private TextBox? textBox;
    private readonly VisualCollection children;
    private Guid? editingId;
    private bool changingText;
    private Size inlineEditorSize;
    private bool selectingText;
    public bool IsSelectingText => selectingText;
    public event EventHandler? TextDragChanged;
    public PixelRect? VisibleImageBounds { get; set; }
    public EditorCanvas()
    {
        children = new VisualCollection(this);
        RenderOptions.SetEdgeMode(this, EdgeMode.Unspecified);
        Focusable = true; ClipToBounds = true; Cursor = Cursors.Cross;
        DataContextChanged += (_, _) =>
        {
            Unsubscribe();
            vm = DataContext as EditorViewModel;
            if (vm is not null) { Width = vm.PixelWidth; Height = vm.PixelHeight; vm.UseInlineText = true; Subscribe(); }
            renderedRevision = -1; cached = null; InvalidateVisual();
        };
        Loaded += (_, _) => { Subscribe(); SyncText(); InvalidateVisual(); };
        Unloaded += (_, _) =>
        {
            Unsubscribe(); cached = null; renderedRevision = -1;
            vm?.CancelGesture();
        };
    }
    private void Subscribe()
    {
        if (vm is null || subscribed) return;
        vm.VisualChanged += OnChanged; vm.CommitTextRequested += CommitText; subscribed = true;
    }
    private void Unsubscribe()
    {
        if (vm is null || !subscribed) return;
        vm.VisualChanged -= OnChanged; vm.CommitTextRequested -= CommitText; subscribed = false;
    }
    protected override int VisualChildrenCount => children.Count;
    protected override Visual GetVisualChild(int index) => children[index];
    private void OnChanged(object? sender, EventArgs args) { SyncText(); InvalidateVisual(); }
    private void SyncText()
    {
        if (changingText) return;
        // Pointer motion changes only the drawing, not layout, unless an inline editor enters/exits.
        if (textBox is null && vm?.InlineText is null) return;
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
                    AcceptsReturn = true, Padding = new Thickness(2), BorderBrush = Ui.Brush("AccentBrush"), BorderThickness = new Thickness(1),
                    VerticalContentAlignment = VerticalAlignment.Top, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    ToolTip = "Ctrl+Enter 提交；Esc 取消；点击其他位置提交" };
                textBox.PreviewKeyDown += (_, e) =>
                {
                    if (e.Key == Key.Escape) { vm?.CancelCurrent(); e.Handled = true; Focus(); }
                    else if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { CommitText(this, EventArgs.Empty); e.Handled = true; Focus(); }
                };
                textBox.LostKeyboardFocus += (_, _) => { if (!changingText) CommitText(this, EventArgs.Empty); };
                textBox.TextChanged += (_, _) => { InvalidateMeasure(); InvalidateArrange(); };
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
        if (textBox is not null && vm?.InlineText is { } a)
        {
            // TextBox/ScrollViewer's desired size can stay at an empty-editor minimum.
            // Reserve actual glyph/line metrics, then grow for typed text and explicit newlines.
            var text = new FormattedText(string.IsNullOrEmpty(textBox.Text) ? "国Ag" : textBox.Text,
                System.Globalization.CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
                new Typeface(textBox.FontFamily, textBox.FontStyle, textBox.FontWeight, textBox.FontStretch), textBox.FontSize, Brushes.Black, 1);
            double width = Math.Min(Math.Max(1, Width - a.Start.X), Math.Max(Math.Max(100, a.FontSize * 3 + 10), Math.Ceiling(text.WidthIncludingTrailingWhitespace) + 10));
            double height = Math.Min(Math.Max(1, Height - a.Start.Y), Math.Max(40, Math.Ceiling(Math.Max(text.Height, textBox.FontFamily.LineSpacing * a.FontSize)) + 10));
            inlineEditorSize = new Size(width, height);
            textBox.Measure(inlineEditorSize);
        }
        return new Size(Width, Height);
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        if (textBox is not null && vm?.InlineText is { } a)
            textBox.Arrange(new Rect(new Point(a.Start.X, a.Start.Y), inlineEditorSize));
        return finalSize;
    }
    protected override void OnRender(DrawingContext context)
    {
        if (vm is null) return;
        if (cached is null || renderedRevision != vm.BaseRevision)
        {
            cached = renderer.BuildDrawing(vm.Image, vm.BaseAnnotations); renderedRevision = vm.BaseRevision;
        }
        context.DrawDrawing(cached);
        if (vm.TextSelection is { IsActive: true } selection)
        {
            var fill = new SolidColorBrush(Color.FromArgb(95, 30, 130, 230)); fill.Freeze();
            foreach (var bounds in selection.Highlights) context.DrawRectangle(fill, null, AnnotationRenderer.Rect(bounds));
            return;
        }
        if (vm.Preview is not null) renderer.DrawAnnotation(context, vm.Preview);
        if (vm.Selected is { } selected)
        {
            var r = renderer.GetBounds(selected);
            double scale = InteractionScale, padding = 4 / scale;
            var pen = new System.Windows.Media.Pen(Brushes.DodgerBlue, 1 / scale) { DashStyle = DashStyles.Dash };
            context.DrawRectangle(null, pen, new Rect(r.X - padding, r.Y - padding, Math.Max(1, r.Width) + padding * 2, Math.Max(1, r.Height) + padding * 2));
            if (vm.CanManipulateAnnotations)
                foreach (double x in new[] { r.X, r.X + r.Width / 2, r.Right }) foreach (double y in new[] { r.Y, r.Y + r.Height / 2, r.Bottom })
                {
                    if (x == r.X + r.Width / 2 && y == r.Y + r.Height / 2) continue;
                    double size = 6 / scale; context.DrawRectangle(Brushes.White, pen, new Rect(x - size / 2, y - size / 2, size, size));
                }
        }
    }
    private double InteractionScale => Math.Max(0.000001, Math.Abs(LayoutTransform.Value.M11));
    private PixelPoint Position(MouseEventArgs e) { var p = e.GetPosition(this); return new(p.X, p.Y); }
    private Cursor PointerCursor(MouseEventArgs e) => vm?.TextSelection is { IsActive: true } text
        ? (text.Document?.Hit(Position(e)) is not null ? Cursors.IBeam : Cursors.Arrow)
        : vm?.PointerHandle(Position(e), InteractionScale) switch
    {
        SelectionHandle.Move => Cursors.SizeAll,
        SelectionHandle.Left or SelectionHandle.Right => Cursors.SizeWE,
        SelectionHandle.Top or SelectionHandle.Bottom => Cursors.SizeNS,
        SelectionHandle.TopLeft or SelectionHandle.BottomRight => Cursors.SizeNWSE,
        SelectionHandle.TopRight or SelectionHandle.BottomLeft => Cursors.SizeNESW,
        _ => Cursors.Cross
    };
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        if (vm?.TextSelection is { IsActive: true } text)
        {
            selectingText = text.Begin(Position(e), e.ClickCount > 1);
            if (selectingText) { CaptureMouse(); TextDragChanged?.Invoke(this, EventArgs.Empty); } e.Handled = true; return;
        }
        CommitText(this, EventArgs.Empty);
        Cursor = PointerCursor(e);
        if (vm?.BeginPointer(Position(e), e.ClickCount > 1, InteractionScale) == true) CaptureMouse();
        e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (selectingText) { vm?.TextSelection?.Move(Position(e)); e.Handled = true; }
        else if (IsMouseCaptured) { vm?.MovePointer(Position(e)); e.Handled = true; }
        else Cursor = PointerCursor(e);
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!IsMouseCaptured) return;
        if (selectingText) { vm?.TextSelection?.Move(Position(e)); vm?.TextSelection?.End(); ReleaseMouseCapture(); selectingText = false; e.Handled = true; return; }
        vm?.EndPointer(Position(e)); ReleaseMouseCapture(); e.Handled = true;
    }
    public void UpdateTextSelection() { if (selectingText) { var p = Mouse.GetPosition(this); vm?.TextSelection?.Move(new(p.X, p.Y)); } }
    public void EndTextSelection() { if (selectingText) { vm?.TextSelection?.End(); ReleaseMouseCapture(); selectingText = false; TextDragChanged?.Invoke(this, EventArgs.Empty); } }
    protected override void OnLostMouseCapture(MouseEventArgs e)
    { if (selectingText) { vm?.TextSelection?.End(); selectingText = false; TextDragChanged?.Invoke(this, EventArgs.Empty); } else vm?.CancelGesture(); base.OnLostMouseCapture(e); }
    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        if (vm?.TextSelection is { IsActive: true } text)
        {
            EndTextSelection();
            var menu = new ContextMenu();
            menu.InputBindings.Add(new KeyBinding(text.CopyCommand, Key.C, ModifierKeys.Control));
            menu.InputBindings.Add(new KeyBinding(text.SelectAllCommand, Key.A, ModifierKeys.Control));
            menu.Items.Add(new MenuItem { Header = "复制文字", InputGestureText = "Ctrl+C", Command = text.CopyCommand });
            menu.Items.Add(new MenuItem { Header = "全选文字", InputGestureText = "Ctrl+A", Command = text.SelectAllCommand });
            var exit = new MenuItem { Header = "返回标注", InputGestureText = "Esc" }; exit.Click += (_, _) => text.Deactivate(); menu.Items.Add(exit);
            ContextMenu = menu; menu.PlacementTarget = this; menu.IsOpen = true; e.Handled = true; return;
        }
        ContextMenu = null; vm?.CancelGesture(); if (IsMouseCaptured) ReleaseMouseCapture(); e.Handled = true;
    }
}
