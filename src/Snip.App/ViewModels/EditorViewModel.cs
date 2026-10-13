using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snip.App.Mvvm;
using Snip.App.Services;
using Snip.Core;
using Snip.Infrastructure;

namespace Snip.App.ViewModels;

public sealed class EditorViewModel : ObservableObject
{
    private readonly AnnotationRenderer renderer;
    private readonly IImageExportService export;
    private IEditorDialogs dialogs;
    private readonly EditHistory history = new();
    private AnnotationTool tool = AnnotationTool.Rectangle;
    private string color = "#FF3B30", status = "拖动画布添加标注；选择工具可移动、删除标注，双击文字可重新编辑。";
    private double strokeWidth = 4, fontSize = 24, zoom = 1;
    private int mosaicSize = 12;
    private PixelPoint pointerStart;
    private Annotation? gestureOriginal;
    private Annotation? preview;
    private bool moving, resizing, exporting, filled;
    private SelectionHandle resizeHandle;
    private PixelRect resizeBounds;
    private Guid? selectedId;

    public EditorViewModel(BitmapSource image, AnnotationRenderer renderer, IImageExportService export, IEditorDialogs dialogs)
    {
        Image = image; this.renderer = renderer; this.export = export; this.dialogs = dialogs;
        UndoCommand = new(() => { CancelGesture(); history.Undo(); selectedId = null; Changed(true); }, () => history.CanUndo && !exporting);
        RedoCommand = new(() => { CancelGesture(); history.Redo(); selectedId = null; Changed(true); }, () => history.CanRedo && !exporting);
        DeleteCommand = new(DeleteSelected, () => selectedId.HasValue && !exporting);
        SaveCommand = new(() => Save(), () => !exporting);
        CopyCommand = new(CopyAsync, e => dialogs.ShowError($"复制失败，请重试：{e.Message}"), () => !exporting);
        ZoomInCommand = new(() => Zoom *= 1.25);
        ZoomOutCommand = new(() => Zoom /= 1.25);
    }

    public event EventHandler? VisualChanged;
    public void RebindDialogs(IEditorDialogs value) => dialogs = value;
    public event EventHandler? CommitTextRequested;
    public Annotation? InlineText { get; private set; }
    public bool UseInlineText { get; set; }
    public BitmapSource Image { get; }
    public int PixelWidth => Image.PixelWidth;
    public int PixelHeight => Image.PixelHeight;
    public string SizeText => $"{PixelWidth} × {PixelHeight} 像素";
    public int BaseRevision { get; private set; }
    public IReadOnlyList<Annotation> BaseAnnotations => history.Current.Where(a =>
        (!(moving || resizing) || gestureOriginal is null || a.Id != gestureOriginal.Id) && a.Id != InlineText?.Id).ToArray();
    public Annotation? Preview => preview;
    public Annotation? Selected => preview?.Id == selectedId ? preview : history.Current.FirstOrDefault(a => a.Id == selectedId);
    public bool IsDirty => history.IsDirty;
    public bool IsExporting => exporting;
    public bool IsDrawing => gestureOriginal is not null;
    public AnnotationTool Tool { get => tool; set { if (Set(ref tool, value)) { CancelGesture(); selectedId = null; Changed(false); } } }
    public string Color
    {
        get => color;
        set
        {
            if (string.IsNullOrWhiteSpace(value)) { Notify(); return; }
            try { _ = ColorConverter.ConvertFromString(value); Set(ref color, value); }
            catch (Exception) { Status = "颜色无效，请输入 #RRGGBB，例如 #168B73。"; Notify(); }
        }
    }
    public double StrokeWidth { get => strokeWidth; set => Set(ref strokeWidth, value); }
    public double FontSize { get => fontSize; set => Set(ref fontSize, value); }
    public int MosaicSize { get => mosaicSize; set => Set(ref mosaicSize, value); }
    public bool Filled { get => filled; set => Set(ref filled, value); }
    public double Zoom { get => zoom; set { if (Set(ref zoom, Math.Clamp(value, 0.1, 4))) Notify(nameof(ZoomText)); } }
    public string ZoomText => $"{Zoom:P0}";
    public string Status { get => status; private set => Set(ref status, value); }
    public double[] StrokeWidths { get; } = [2, 4, 6, 8];
    public double[] FontSizes { get; } = [16, 20, 24, 32, 48, 64];
    public int[] MosaicSizes { get; } = [8, 12, 20, 32];
    public string[] Colors { get; } = ["#FF3B30", "#FF9500", "#FFCC00", "#168B73", "#007AFF", "#AF52DE", "#FFFFFF", "#202020"];
    public RelayCommand UndoCommand { get; }
    public RelayCommand RedoCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand SaveCommand { get; }
    public AsyncCommand CopyCommand { get; }
    public RelayCommand ZoomInCommand { get; }
    public RelayCommand ZoomOutCommand { get; }

    private PixelPoint Clamp(PixelPoint p) => new(Math.Clamp(p.X, 0, PixelWidth), Math.Clamp(p.Y, 0, PixelHeight));

    public bool BeginPointer(PixelPoint point, bool doubleClick)
    {
        if (exporting) return false;
        CancelGesture();
        point = Clamp(point); pointerStart = point;
        if (Tool == AnnotationTool.Select)
        {
            if (Selected is { } current)
            {
                resizeBounds = renderer.GetBounds(current);
                resizeHandle = SelectionGeometry.HitTest(resizeBounds, point, 8 / Zoom);
                if (resizeHandle is not SelectionHandle.New and not SelectionHandle.Move)
                {
                    gestureOriginal = current; preview = current; resizing = true; Changed(true); return true;
                }
            }
            var hit = history.Current.Reverse().FirstOrDefault(a => renderer.HitTest(a, point, 6 / Zoom));
            selectedId = hit?.Id;
            if (hit is null) { Changed(false); return false; }
            if (doubleClick && hit.Tool == AnnotationTool.Text) { EditText(hit); return false; }
            moving = true; gestureOriginal = hit; preview = hit; Changed(true); return true;
        }
        selectedId = null;
        if (Tool == AnnotationTool.Text)
        {
            if (UseInlineText) { InlineText = Create(point); Changed(true); return false; }
            var input = dialogs.EditText(string.Empty, Color, FontSize);
            if (input is not null && !string.IsNullOrWhiteSpace(input.Text))
                history.Commit(history.Current.Append(Create(point) with { Text = input.Text, Color = input.Color, FontSize = input.FontSize }));
            Changed(true); return false;
        }
        gestureOriginal = Create(point); preview = gestureOriginal;
        Changed(false); return true;
    }

    private Annotation Create(PixelPoint p) => new(Guid.NewGuid(), Tool, p, p, Color, StrokeWidth, FontSize, MosaicSize, string.Empty, [p]) { Filled = Filled };

    public void MovePointer(PixelPoint point)
    {
        if (gestureOriginal is null) return;
        point = Clamp(point);
        if (resizing)
        {
            var target = SelectionGeometry.Drag(resizeBounds, resizeHandle, pointerStart, point, new(0, 0, PixelWidth, PixelHeight));
            if (target.Width >= 4 && target.Height >= 4) preview = gestureOriginal.Resize(resizeBounds, target);
        }
        else if (moving)
        {
            var delta = point - pointerStart;
            var bounds = renderer.GetBounds(gestureOriginal);
            delta = new(Math.Clamp(delta.X, -bounds.X, Math.Max(-bounds.X, PixelWidth - bounds.Right)),
                Math.Clamp(delta.Y, -bounds.Y, Math.Max(-bounds.Y, PixelHeight - bounds.Bottom)));
            preview = gestureOriginal.Translate(delta);
        }
        else if (Tool == AnnotationTool.Pen)
        {
            var points = preview!.Points;
            if (Math.Abs(point.X - points[^1].X) + Math.Abs(point.Y - points[^1].Y) < 0.5) return;
            preview = preview with { End = point, Points = points.Append(point).ToArray() };
        }
        else preview = gestureOriginal with { End = point };
        Changed(false);
    }

    public void EndPointer(PixelPoint point)
    {
        if (gestureOriginal is null) return;
        MovePointer(point);
        var final = preview!;
        if (moving || resizing)
        {
            if (final.Start != gestureOriginal.Start || final.End != gestureOriginal.End || final.FontSize != gestureOriginal.FontSize)
                history.Commit(history.Current.Select(a => a.Id == final.Id ? final : a));
        }
        else if (final.Tool == AnnotationTool.Pen || (final.Tool == AnnotationTool.Arrow ? final.Start != final.End : final.Bounds.Width >= 1 && final.Bounds.Height >= 1))
            history.Commit(history.Current.Append(final));
        gestureOriginal = preview = null; moving = resizing = false;
        Changed(true);
    }

    public void CancelGesture()
    {
        if (gestureOriginal is null) return;
        gestureOriginal = preview = null; moving = resizing = false; Changed(true);
    }

    private void EditText(Annotation annotation)
    {
        if (UseInlineText) { InlineText = annotation; Changed(true); return; }
        var input = dialogs.EditText(annotation.Text, annotation.Color, annotation.FontSize);
        if (input is null || string.IsNullOrWhiteSpace(input.Text)) return;
        var next = annotation with { Text = input.Text, Color = input.Color, FontSize = input.FontSize };
        history.Commit(history.Current.Select(a => a.Id == annotation.Id ? next : a));
        Changed(true);
    }

    private void DeleteSelected()
    {
        CancelGesture();
        if (selectedId is null) return;
        history.Commit(history.Current.Where(a => a.Id != selectedId)); selectedId = null; Changed(true);
    }

    public void Fit(double width, double height) => Zoom = Math.Min(1, Math.Min(Math.Max(1, width - 32) / PixelWidth, Math.Max(1, height - 32) / PixelHeight));

    public bool Save()
    {
        CommitTextRequested?.Invoke(this, EventArgs.Empty);
        CancelGesture();
        var path = dialogs.ChoosePngPath();
        if (path is null) return false;
        try
        {
            export.SavePng(renderer.Render(Image, history.Current), path);
            history.MarkExported(); Status = $"已保存：{path}"; return true;
        }
        catch (Exception e) { dialogs.ShowError($"保存失败，编辑内容已保留：{e.Message}"); return false; }
    }

    private async Task CopyAsync()
    {
        CommitTextRequested?.Invoke(this, EventArgs.Empty);
        CancelGesture(); exporting = true; RefreshCommands();
        try
        {
            var result = renderer.Render(Image, history.Current);
            await export.CopyAsync(result);
            history.MarkExported(); Status = "截图已复制到剪贴板，可粘贴到画图或聊天窗口。";
        }
        finally { exporting = false; RefreshCommands(); }
    }

    public bool CanClose()
    {
        CommitTextRequested?.Invoke(this, EventArgs.Empty);
        if (exporting) return false;
        CancelGesture();
        if (!IsDirty) return true;
        return dialogs.ConfirmClose() switch { CloseChoice.Discard => true, CloseChoice.Save => Save(), _ => false };
    }

    public void CommitInlineText(string text)
    {
        if (InlineText is not { } annotation) return;
        InlineText = null;
        if (!string.IsNullOrWhiteSpace(text))
        {
            var next = annotation with { Text = text };
            var existing = history.Current.Any(a => a.Id == next.Id);
            if (!existing || next.Text != annotation.Text)
                history.Commit(existing ? history.Current.Select(a => a.Id == next.Id ? next : a) : history.Current.Append(next));
        }
        Changed(true);
    }

    public bool CancelCurrent()
    {
        if (InlineText is not null) { InlineText = null; Changed(true); return true; }
        if (IsDrawing) { CancelGesture(); return true; }
        return false;
    }

    public BitmapSource Compose()
    {
        CommitTextRequested?.Invoke(this, EventArgs.Empty); CancelGesture();
        return renderer.Render(Image, history.Current);
    }
    public async Task<bool> CopyAndMarkAsync()
    {
        if (exporting) return false;
        await CopyAsync(); return true;
    }
    public void AddSticker(ReadOnlyMemory<byte> png, double width, double height)
    {
        CommitTextRequested?.Invoke(this, EventArgs.Empty); CancelGesture();
        double scale = Math.Min(1, Math.Min(PixelWidth * 0.5 / width, PixelHeight * 0.5 / height));
        double w = Math.Max(1, width * scale), h = Math.Max(1, height * scale);
        var start = new PixelPoint((PixelWidth - w) / 2, (PixelHeight - h) / 2);
        var a = new Annotation(Guid.NewGuid(), AnnotationTool.Sticker, start, start + new PixelPoint(w, h), Color, 1, 24, 12, "", []) { StickerPng = png.ToArray() };
        history.Commit(history.Current.Append(a)); selectedId = a.Id; Tool = AnnotationTool.Select; selectedId = a.Id; Changed(true);
    }

    private void Changed(bool baseChanged)
    {
        if (baseChanged) BaseRevision++;
        RefreshCommands(); VisualChanged?.Invoke(this, EventArgs.Empty);
    }
    private void RefreshCommands()
    {
        UndoCommand.Refresh(); RedoCommand.Refresh(); DeleteCommand.Refresh(); SaveCommand.Refresh(); CopyCommand.Refresh();
    }
}
