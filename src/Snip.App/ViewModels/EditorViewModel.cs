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
    private readonly IEditorDialogs dialogs;
    private readonly EditHistory history = new();
    private AnnotationTool tool = AnnotationTool.Rectangle;
    private string color = "#FF3B30", status = "拖动画布添加标注；选择工具可移动、删除标注，双击文字可重新编辑。";
    private double strokeWidth = 4, fontSize = 24, zoom = 1;
    private int mosaicSize = 12;
    private PixelPoint pointerStart;
    private Annotation? gestureOriginal;
    private Annotation? preview;
    private bool moving, exporting;
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
    public BitmapSource Image { get; }
    public int PixelWidth => Image.PixelWidth;
    public int PixelHeight => Image.PixelHeight;
    public string SizeText => $"{PixelWidth} × {PixelHeight} 像素";
    public int BaseRevision { get; private set; }
    public IReadOnlyList<Annotation> BaseAnnotations => moving && gestureOriginal is not null
        ? history.Current.Where(a => a.Id != gestureOriginal.Id).ToArray() : history.Current;
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
            try { _ = ColorConverter.ConvertFromString(value); Set(ref color, value); }
            catch (Exception) { Status = "颜色无效，请输入 #RRGGBB，例如 #168B73。"; Notify(); }
        }
    }
    public double StrokeWidth { get => strokeWidth; set => Set(ref strokeWidth, value); }
    public double FontSize { get => fontSize; set => Set(ref fontSize, value); }
    public int MosaicSize { get => mosaicSize; set => Set(ref mosaicSize, value); }
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
            var hit = history.Current.Reverse().FirstOrDefault(a => renderer.HitTest(a, point, 6 / Zoom));
            selectedId = hit?.Id;
            if (hit is null) { Changed(false); return false; }
            if (doubleClick && hit.Tool == AnnotationTool.Text) { EditText(hit); return false; }
            moving = true; gestureOriginal = hit; preview = hit; Changed(true); return true;
        }
        selectedId = null;
        if (Tool == AnnotationTool.Text)
        {
            var input = dialogs.EditText(string.Empty, Color, FontSize);
            if (input is not null && !string.IsNullOrWhiteSpace(input.Text))
                history.Commit(history.Current.Append(Create(point) with { Text = input.Text, Color = input.Color, FontSize = input.FontSize }));
            Changed(true); return false;
        }
        gestureOriginal = Create(point); preview = gestureOriginal;
        Changed(false); return true;
    }

    private Annotation Create(PixelPoint p) => new(Guid.NewGuid(), Tool, p, p, Color, StrokeWidth, FontSize, MosaicSize, string.Empty, [p]);

    public void MovePointer(PixelPoint point)
    {
        if (gestureOriginal is null) return;
        point = Clamp(point);
        if (moving)
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
        if (moving)
        {
            if (final.Start != gestureOriginal.Start)
                history.Commit(history.Current.Select(a => a.Id == final.Id ? final : a));
        }
        else if (final.Tool == AnnotationTool.Pen || (final.Tool == AnnotationTool.Arrow ? final.Start != final.End : final.Bounds.Width >= 1 && final.Bounds.Height >= 1))
            history.Commit(history.Current.Append(final));
        gestureOriginal = preview = null; moving = false;
        Changed(true);
    }

    public void CancelGesture()
    {
        if (gestureOriginal is null) return;
        gestureOriginal = preview = null; moving = false; Changed(true);
    }

    private void EditText(Annotation annotation)
    {
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
        if (exporting) return false;
        CancelGesture();
        if (!IsDirty) return true;
        return dialogs.ConfirmClose() switch { CloseChoice.Discard => true, CloseChoice.Save => Save(), _ => false };
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
