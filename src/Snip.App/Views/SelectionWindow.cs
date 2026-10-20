using System.Windows;
using System.Windows.Controls;
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
    private readonly Canvas overlay = new();
    private readonly EditorToolbar toolbar = new();
    private EditorCanvas? editorCanvas;
    private readonly TextBlock hint;
    private readonly Border hintFrame;
    public DisplayInfo Display { get; }
    public SelectionWindow(SelectionSession session, IDesktopCaptureService capture, DisplayInfo display)
    {
        Ui.ApplyWindow(this);
        this.session = session; Display = display;
        Title = "自用截图 · 选区编辑"; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; Topmost = true; Background = Brushes.Black;
        Width = display.Bounds.Width; Height = display.Bounds.Height;
        var root = new Grid(); root.Children.Add(new SelectionSurface(session, capture, display)); root.Children.Add(overlay);
        toolbar.Name = "SelectionToolbar";
        toolbar.Visibility = Visibility.Collapsed;
        toolbar.SizeChanged += (_, _) => Position(this, EventArgs.Empty);
        toolbar.CompleteRequested += (_, _) => session.Confirm(); toolbar.CancelRequested += (_, _) => Cancel();
        toolbar.WindowRequested += (_, _) => session.Request(SelectionAction.Editor);
        toolbar.SelectionRequested += (_, _) => session.AdjustSelection();
        toolbar.StickerRequested += (_, _) => session.AddSticker(this);
        toolbar.PinRequested += (_, _) => session.Request(SelectionAction.Pin);
        toolbar.ScrollRequested += mode => session.Request(SelectionAction.Scroll, mode);
        toolbar.ShareRequested += (_, _) => session.Request(SelectionAction.Share);
        toolbar.OcrRequested += (_, _) => session.Request(SelectionAction.Ocr);
        toolbar.TranslateRequested += (_, _) => session.Request(SelectionAction.Translate);
        overlay.Children.Add(toolbar);
        hint = new TextBlock { Text = "悬停自动选择区域 · Enter复制 · 拖动重新选择 · Esc取消", Foreground = Brushes.White, FontSize = 12, Padding = new Thickness(12, 8, 12, 8), TextWrapping = TextWrapping.Wrap, IsHitTestVisible = false };
        hintFrame = new Border { Name = "SelectionHint", Child = hint, CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Color.FromArgb(225, 36, 50, 57)), IsHitTestVisible = false };
        overlay.Children.Add(hintFrame); Canvas.SetLeft(hintFrame, 16); Canvas.SetTop(hintFrame, 16); Content = root;
        SourceInitialized += (_, _) => capture.PlaceOverlay(this, display.Bounds);
        PreviewKeyDown += OnKey; session.EditorChanged += RefreshEditor; session.ViewModel.Changed += Position;
        SizeChanged += (_, _) => { Position(this, EventArgs.Empty); PositionEditor(); };
        Closed += (_, _) =>
        {
            session.EditorChanged -= RefreshEditor; session.ViewModel.Changed -= Position;
            if (editorCanvas is not null) editorCanvas.DataContext = null;
            toolbar.DataContext = null;
            session.Cancel();
        };
    }
    private void Cancel()
    {
        if (session.Editor?.IsDirty == true && MessageBox.Show(this, "放弃当前截图和标注？", "取消截图", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        session.Cancel();
    }
    private void RefreshEditor(object? sender, EventArgs e)
    {
        if (editorCanvas is not null) { overlay.Children.Remove(editorCanvas); editorCanvas = null; }
        if (session.Editing && session.Editor is { } vm)
        {
            var region = session.ViewModel.Selection;
            var visible = region.Intersect(Display.Bounds).Translate(new(-region.X, -region.Y));
            editorCanvas = new EditorCanvas { VisibleImageBounds = visible, DataContext = vm, ClipToBounds = true };
            overlay.Children.Insert(0, editorCanvas); toolbar.DataContext = vm; PositionEditor();
        }
        else toolbar.DataContext = null;
        Position(this, EventArgs.Empty);
    }
    private void PositionEditor()
    {
        if (editorCanvas is null || ActualWidth <= 0) return;
        var r = session.ViewModel.Selection; double sx = ActualWidth / Display.Bounds.Width, sy = ActualHeight / Display.Bounds.Height;
        editorCanvas.LayoutTransform = new ScaleTransform(sx, sy);
        Canvas.SetLeft(editorCanvas, (r.X - Display.Bounds.X) * sx); Canvas.SetTop(editorCanvas, (r.Y - Display.Bounds.Y) * sy);
    }
    private void Position(object? sender, EventArgs e)
    {
        hint.Text = session.ViewModel.SizeText + (session.Editing ? " · 选区内标注 · Enter或对勾复制完成" : " · Enter复制 · 拖动重新选择 · 单击进入标注");
        var r = session.ViewModel.Selection;
        var selectedDisplay = session.ViewModel.Frame.Displays.OrderByDescending(d => { var i = r.Intersect(d.Bounds); return i.Width * i.Height; }).First();
        bool visible = session.Editing && selectedDisplay.Name == Display.Name;
        toolbar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        // Measure in overlay DIPs, including wrapped text on smaller/scaled displays.
        hintFrame.MaxWidth = Math.Max(1, ActualWidth - 32);
        hintFrame.Measure(new Size(hintFrame.MaxWidth, double.PositiveInfinity));
        Canvas.SetLeft(hintFrame, 16); Canvas.SetTop(hintFrame, 16);
        if (!visible) return;
        toolbar.Width = Math.Min(1000, Math.Max(200, ActualWidth - 24)); toolbar.Measure(new Size(toolbar.Width, double.PositiveInfinity));
        // Bindings/templates may finish layout after the first measure. SizeChanged repairs
        // the placement using the rendered height, without a per-frame LayoutUpdated handler.
        double toolbarHeight = Math.Max(toolbar.DesiredSize.Height, toolbar.ActualHeight);
        double sx = ActualWidth / Display.Bounds.Width, sy = ActualHeight / Display.Bounds.Height;
        double x = Math.Clamp((r.X - Display.Bounds.X) * sx, 12, Math.Max(12, ActualWidth - toolbar.Width - 12));
        double below = (r.Bottom - Display.Bounds.Y) * sy + 10;
        double y = below + toolbarHeight <= ActualHeight - 12 ? below : (r.Y - Display.Bounds.Y) * sy - toolbarHeight - 10;
        y = Math.Clamp(y, 12, Math.Max(12, ActualHeight - toolbarHeight - 12));
        var toolbarBounds = new Rect(x, y, toolbar.Width, toolbarHeight);
        var hintBounds = new Rect(new Point(16, 16), hintFrame.DesiredSize);
        var clearance = toolbarBounds; clearance.Inflate(8, 8);
        if (clearance.IntersectsWith(hintBounds))
        {
            double hintBelow = toolbarBounds.Bottom + 8;
            double hintAbove = toolbarBounds.Top - hintBounds.Height - 8;
            if (hintBelow + hintBounds.Height <= ActualHeight - 16)
                Canvas.SetTop(hintFrame, hintBelow);
            else if (hintAbove >= 16)
                Canvas.SetTop(hintFrame, hintAbove);
            else
                // When neither side has room, reserve the top hint row for both controls.
                y = hintBounds.Bottom + 8;
        }
        Canvas.SetLeft(toolbar, x); Canvas.SetTop(toolbar, y);
    }
    private void OnKey(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;
        if (e.Key == Key.Escape) { if (session.Editor?.CancelCurrent() != true) Cancel(); e.Handled = true; }
        else if (e.Key == Key.Enter) { session.Confirm(); e.Handled = true; }
        else if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        { session.Nudge(e.Key == Key.Left ? -1 : e.Key == Key.Right ? 1 : 0, e.Key == Key.Up ? -1 : e.Key == Key.Down ? 1 : 0); e.Handled = true; }
        else if (session.Editor is { } vm && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            var command = e.Key switch { Key.Z => vm.UndoCommand, Key.Y => vm.RedoCommand, Key.S => vm.SaveCommand, _ => null };
            if (command is not null) { command.Execute(null); e.Handled = true; }
        }
        else if (e.Key == Key.Delete) { session.Editor?.DeleteCommand.Execute(null); e.Handled = true; }
    }
}

internal sealed class SelectionSurface : FrameworkElement
{
    private readonly SelectionSession session;
    private readonly SelectionViewModel vm;
    private readonly IDesktopCaptureService capture;
    private readonly DisplayInfo display;
    private readonly BitmapSource image;
    private PixelPoint dragStart, cursor;
    private bool newSelection;
    public SelectionSurface(SelectionSession session, IDesktopCaptureService capture, DisplayInfo display)
    {
        this.session = session; vm = session.ViewModel; this.capture = capture; this.display = display; ClipToBounds = true; Focusable = true;
        cursor = capture.CursorPosition;
        var b = display.Bounds; var desktop = vm.Frame.Bounds;
        image = new CroppedBitmap(vm.Frame.Image, new Int32Rect((int)(b.X - desktop.X), (int)(b.Y - desktop.Y), (int)b.Width, (int)b.Height)); image.Freeze();
        vm.Changed += OnChanged; session.EditorChanged += OnChanged;
        Unloaded += (_, _) => { vm.Changed -= OnChanged; session.EditorChanged -= OnChanged; if (IsMouseCaptured) ReleaseMouseCapture(); };
    }
    private void OnChanged(object? sender, EventArgs e) => InvalidateVisual();
    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight); dc.DrawImage(image, bounds);
        double sx = ActualWidth / display.Bounds.Width, sy = ActualHeight / display.Bounds.Height;
        var r = vm.DisplayedSelection;
        var local = new Rect((r.X - display.Bounds.X) * sx, (r.Y - display.Bounds.Y) * sy, r.Width * sx, r.Height * sy);
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(135, 0, 0, 0)), null,
            new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(bounds), new RectangleGeometry(local)));
        if (!r.IsEmpty) dc.DrawRectangle(null, new System.Windows.Media.Pen(Ui.Brush("AccentBrush"), 1.5), local);
        if (!session.Editing)
        {
            if (!r.IsEmpty && !vm.Selection.IsEmpty)
                foreach (double hx in new[] { local.Left, (local.Left + local.Right) / 2, local.Right })
                foreach (double hy in new[] { local.Top, (local.Top + local.Bottom) / 2, local.Bottom })
                    if (hx != (local.Left + local.Right) / 2 || hy != (local.Top + local.Bottom) / 2)
                        dc.DrawRectangle(Brushes.White, new System.Windows.Media.Pen(Ui.Brush("AccentBrush"), 1), new Rect(hx - 3, hy - 3, 6, 6));
            double x = (cursor.X - display.Bounds.X) * sx, y = (cursor.Y - display.Bounds.Y) * sy;
            if (x < 0 || y < 0 || x >= ActualWidth || y >= ActualHeight) return;
            int px = Math.Clamp((int)(cursor.X - display.Bounds.X), 0, image.PixelWidth - 1), py = Math.Clamp((int)(cursor.Y - display.Bounds.Y), 0, image.PixelHeight - 1);
            int left = Math.Max(0, px - 8), top = Math.Max(0, py - 8), w = Math.Min(17, image.PixelWidth - left), h = Math.Min(17, image.PixelHeight - top);
            var zoom = new CroppedBitmap(image, new Int32Rect(left, top, w, h));
            double mx = Math.Clamp(x + 22, 0, Math.Max(0, ActualWidth - 120)), my = Math.Clamp(y + 22, 0, Math.Max(0, ActualHeight - 120));
            dc.DrawRoundedRectangle(Brushes.White, new System.Windows.Media.Pen(Ui.Brush("AccentBrush"), 1.5), new Rect(mx, my, 112, 112), 6, 6);
            dc.DrawImage(zoom, new Rect(mx + 1, my + 1, 110, 110));
            dc.DrawLine(new System.Windows.Media.Pen(Brushes.Red, 1), new(mx + 56, my), new(mx + 56, my + 112));
            dc.DrawLine(new System.Windows.Media.Pen(Brushes.Red, 1), new(mx, my + 56), new(mx + 112, my + 56));
        }
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    { if (session.Editing) return; Focus(); dragStart = capture.CursorPosition; newSelection = SelectionGeometry.HitTest(vm.Selection, dragStart) == SelectionHandle.New; vm.Begin(dragStart); CaptureMouse(); e.Handled = true; }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (session.Editing) return;
        cursor = capture.CursorPosition;
        if (IsMouseCaptured) vm.Move(cursor);
        else session.UpdateHoveredTarget(cursor);
        Cursor = SelectionGeometry.HitTest(vm.Selection, cursor) switch
        {
            SelectionHandle.Move => Cursors.SizeAll,
            SelectionHandle.Left or SelectionHandle.Right => Cursors.SizeWE,
            SelectionHandle.Top or SelectionHandle.Bottom => Cursors.SizeNS,
            SelectionHandle.TopLeft or SelectionHandle.BottomRight => Cursors.SizeNWSE,
            SelectionHandle.TopRight or SelectionHandle.BottomLeft => Cursors.SizeNESW,
            _ => Cursors.Cross
        }; InvalidateVisual();
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!IsMouseCaptured) return;
        var p = capture.CursorPosition; vm.End(p); ReleaseMouseCapture();
        if (newSelection && Math.Abs(p.X - dragStart.X) + Math.Abs(p.Y - dragStart.Y) < 3 && !vm.HoveredSelection.IsEmpty) vm.SetSelection(vm.HoveredSelection);
        session.BeginEditing(); e.Handled = true;
    }
    protected override void OnLostMouseCapture(MouseEventArgs e) { vm.CancelDrag(); base.OnLostMouseCapture(e); }
    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e) { if (!session.Editing) session.Cancel(); e.Handled = true; }
}
