using System.ComponentModel;
using System.IO;
using System.Windows.Media.Imaging;
using Snip.App.Mvvm;
using Snip.Core;
using Snip.Infrastructure;

namespace Snip.App.ViewModels;

/// <summary>Opt-in local OCR. Cached geometry is tied to the committed, composed image, never hidden source text.</summary>
public sealed class ImageTextSelectionViewModel : ObservableObject, IDisposable
{
    private readonly EditorViewModel editor;
    private readonly IOcrService ocr;
    private readonly ITextClipboardService clipboard;
    private readonly DiagnosticJournal? diagnostics;
    private CancellationTokenSource? operation;
    private IReadOnlyList<Annotation>? snapshot;
    private ImageTextGlyph? anchor;
    private bool active, busy, disposed, preparing;
    private int generation, start, end;
    private string status = "点击选取文字，在图片上拖选后复制。";
    public ImageTextSelectionViewModel(EditorViewModel editor, IOcrService ocr, ITextClipboardService clipboard, DiagnosticJournal? diagnostics = null)
    {
        this.editor = editor; this.ocr = ocr; this.clipboard = clipboard; this.diagnostics = diagnostics;
        ToggleCommand = new(() => { if (IsActive) Deactivate(); else _ = StartAsync(); }, () => !disposed && !editor.IsExporting);
        RetryCommand = new(() => { ClearDocument(); _ = StartAsync(); }, () => IsActive && !Busy && !disposed);
        SelectAllCommand = new(() => { if (Document is { } doc) SetRange(0, doc.Text.Length); }, () => IsActive && Document is not null);
        CopyCommand = new(CopyAsync, _ => { if (!disposed) Status = "复制文字失败，选字已保留，请重试。"; }, () => IsActive && !Busy && end > start && !disposed);
        editor.VisualChanged += EditorChanged; editor.PropertyChanged += EditorPropertyChanged;
    }
    public event EventHandler? VisualChanged;
    public ImageTextDocument? Document { get; private set; }
    public bool IsActive { get => active; private set { if (Set(ref active, value)) Refresh(); } }
    public bool Busy { get => busy; private set { if (Set(ref busy, value)) Refresh(); } }
    public string Status { get => status; private set => Set(ref status, value); }
    public IReadOnlyList<PixelRect> Highlights { get; private set; } = [];
    public string SelectedText => Document is { } doc && end > start ? doc.Text[start..end] : "";
    public RelayCommand ToggleCommand { get; }
    public RelayCommand RetryCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public AsyncCommand CopyCommand { get; }
    private async Task StartAsync()
    {
        if (disposed || editor.IsExporting) return;
        CancelOperation(); IsActive = true;
        preparing = true;
        try { editor.CommitPendingEdits(); }
        finally { preparing = false; }
        if (Document is not null && ReferenceEquals(snapshot, editor.CommittedAnnotations))
        { Status = "拖选图片里的文字，Ctrl+C 复制；双击选词，Ctrl+A 全选，Esc 返回标注。"; return; }
        ClearDocument(); snapshot = editor.CommittedAnnotations;
        var annotations = snapshot; var image = editor.Image;
        if (!image.IsFrozen) { image = image.Clone(); image.Freeze(); }
        int revision = ++generation;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45)); operation = cancellation;
        var token = cancellation.Token; Busy = true; Status = "正在本机识别文字，可再次点击“选取文字”取消…";
        try
        {
            var document = await Task.Run(async () =>
            {
                token.ThrowIfCancellationRequested();
                var rendered = new AnnotationRenderer().Render(image, annotations);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(rendered));
                using var stream = new MemoryStream(); encoder.Save(stream);
                token.ThrowIfCancellationRequested();
                var payload = new ImagePayload(stream.ToArray(), rendered.PixelWidth, rendered.PixelHeight);
                IReadOnlyList<string> candidates = ocr is IOcrEnhancement { Enabled: true, IsInstalled: true }
                    ? [OcrLanguageSelection.Automatic] : OcrLanguageSelection.Candidates(ocr.Languages, OcrLanguageSelection.Automatic);
                if (candidates.Count == 0) throw new InvalidOperationException("未安装本机 OCR 语言组件，可在文字识别设置中添加中文 / 英语或下载增强 OCR。");
                OcrResult result = new([]);
                foreach (string candidate in candidates)
                {
                    token.ThrowIfCancellationRequested();
                    result = await ocr.RecognizeAsync(payload, candidate, token).ConfigureAwait(false);
                    if (result.Regions.Any(r => !string.IsNullOrWhiteSpace(r.Text))) break;
                }
                token.ThrowIfCancellationRequested();
                return ImageTextDocument.Create(result, payload.Width, payload.Height, annotations.Where(a => a.Tool == AnnotationTool.Mosaic).Select(a => a.Bounds).ToArray());
            }, token).WaitAsync(token);
            if (disposed || revision != generation || !IsActive || !ReferenceEquals(snapshot, editor.CommittedAnnotations)) return;
            Document = document;
            Status = document.Glyphs.Count == 0 ? "没有识别到文字；可提高图片清晰度，或在文字识别中调整本机 OCR 后重新打开。" : "拖选文字后 Ctrl+C 复制；双击选词，Ctrl+A 全选，Esc 返回标注。";
            Refresh(); VisualChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        { if (!disposed && revision == generation && IsActive) Status = "本机识别已超时，可点击重新识别，或退出选字继续标注。"; }
        catch (Exception error)
        {
            diagnostics?.Record("image-text-selection", "recognition-failed", error:error);
            if (!disposed && revision == generation && IsActive) Status = "本机文字识别未完成，请检查中文 / 英语 OCR 组件，或在文字识别中下载增强 OCR 后重试。";
        }
        finally { if (revision == generation) { operation = null; Busy = false; } }
    }
    public bool Begin(PixelPoint point, bool word = false)
    {
        if (!IsActive || Busy || Document?.Hit(point) is not { } hit) { SetRange(0, 0); return false; }
        anchor = hit;
        if (word) { SetRange(hit.WordStart, hit.WordStart + hit.WordLength); return false; }
        SetRange(hit.Start, hit.Start + hit.Length); return true;
    }
    public void Move(PixelPoint point)
    {
        if (anchor is not { } first || Document?.Hit(point, nearest:true) is not { } last) return;
        SetRange(Math.Min(first.Start, last.Start), Math.Max(first.Start + first.Length, last.Start + last.Length));
    }
    public void End() => anchor = null;
    private void SetRange(int first, int last)
    {
        start = first; end = last;
        Highlights = Document?.Highlights(start, end) ?? [];
        Notify(nameof(SelectedText)); CopyCommand.Refresh(); VisualChanged?.Invoke(this, EventArgs.Empty);
    }
    private async Task CopyAsync()
    {
        string text = SelectedText;
        if (string.IsNullOrWhiteSpace(text)) return;
        int revision = generation;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5)); operation = cancellation;
        try
        {
            await clipboard.CopyAsync(text, cancellation.Token);
            if (!disposed && revision == generation && IsActive) Status = "已复制选中文字。";
        }
        finally { if (ReferenceEquals(operation, cancellation)) operation = null; }
    }
    private void CancelOperation() { ++generation; operation?.Cancel(); operation = null; Busy = false; }
    public void Deactivate()
    {
        CancelOperation(); IsActive = false; anchor = null; SetRange(0, 0);
    }
    private void ClearDocument() { Document = null; snapshot = null; anchor = null; SetRange(0, 0); Refresh(); }
    private void EditorChanged(object? sender, EventArgs args)
    {
        if (preparing || snapshot is null) return;
        if (editor.IsDrawing || editor.InlineText is not null || !ReferenceEquals(snapshot, editor.CommittedAnnotations))
        { Deactivate(); ClearDocument(); }
    }
    private void EditorPropertyChanged(object? sender, PropertyChangedEventArgs args)
    { if (args.PropertyName == nameof(editor.Tool) && IsActive) Deactivate(); }
    private void Refresh() { ToggleCommand.Refresh(); RetryCommand.Refresh(); SelectAllCommand.Refresh(); CopyCommand.Refresh(); }
    public void Dispose()
    {
        if (disposed) return; disposed = true; Deactivate(); ClearDocument();
        editor.VisualChanged -= EditorChanged; editor.PropertyChanged -= EditorPropertyChanged;
    }
}
