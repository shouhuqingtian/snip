using System.Windows;
using System.Windows.Media.Imaging;
using Snip.App.Mvvm;
using Snip.Infrastructure;
using Snip.Translation;
using Snip.Core;
using System.IO;

namespace Snip.App.ViewModels;

public sealed class TextResultViewModel : ObservableObject, IDisposable
{
    private readonly ImagePayload image;
    private readonly IOcrService ocr;
    private readonly ModelStore models;
    private readonly ITranslationService translator;
    private CancellationTokenSource? operation;
    private string source = "", translated = "", status = "选择 OCR 语言后识别；翻译模型首次使用需要下载。", direction = "en-zh";
    private string? language;
    private bool busy;
    private double progress;
    public TextResultViewModel(BitmapSource image, IOcrService ocr, ModelStore models, ITranslationService translator)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = new MemoryStream(); encoder.Save(stream);
        this.image = new(stream.ToArray(), image.PixelWidth, image.PixelHeight); this.ocr = ocr; this.models = models; this.translator = translator;
        try { Languages = ocr.Languages; } catch (Exception e) { Languages = []; status = e.Message; }
        language = Languages.FirstOrDefault(x => x.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) ?? Languages.FirstOrDefault();
        OcrCommand = new(() => Run(async token =>
        {
            var result = await ocr.RecognizeAsync(this.image, Language, token);
            Source = string.Join(Environment.NewLine, result.Regions.Select(r => r.Text));
            Status = string.IsNullOrWhiteSpace(Source) ? "没有识别到文字；可更换语言或重新截图。" : "文字已识别，可修正原文后翻译或复制。";
            if (AutoTranslate && !string.IsNullOrWhiteSpace(Source))
            {
                Direction = Source.Any(c => c >= 0x4e00 && c <= 0x9fff) ? "zh-en" : "en-zh";
                if (await models.IsInstalledAsync(Direction, token)) { Translated = await translator.TranslateAsync(Source, Direction, token); Status = "离线识别和翻译完成。"; }
                else Status = "原文已识别，请先下载翻译模型。";
            }
        }), Report, () => !Busy);
        TranslateCommand = new(() => Run(async token => { Translated = await translator.TranslateAsync(Source, Direction, token); Status = "离线翻译完成。"; }), Report, () => !Busy && !string.IsNullOrWhiteSpace(Source));
        DownloadCommand = new(() => Run(async token =>
        {
            await models.InstallAsync(new Progress<DownloadProgress>(p => { Progress = p.CompletedBytes * 100.0 / p.TotalBytes; Status = $"下载模型 {p.Direction}：{Progress:F1}%（{p.TotalBytes / 1024 / 1024} MB）"; }), token);
            Status = "两个方向的模型已校验，可断网翻译。";
        }), Report, () => !Busy);
        CopySourceCommand = new(() => Copy(Source)); CopyTranslationCommand = new(() => Copy(Translated));
        CancelCommand = new(() => operation?.Cancel(), () => Busy);
    }
    public IReadOnlyList<string> Languages { get; }
    public bool AutoTranslate { get; init; }
    public string? Language { get => language; set => Set(ref language, value); }
    public string Direction { get => direction; set => Set(ref direction, value); }
    public string Source { get => source; set { if (Set(ref source, value)) TranslateCommand.Refresh(); } }
    public string Translated { get => translated; private set => Set(ref translated, value); }
    public string Status { get => status; private set => Set(ref status, value); }
    public bool Busy { get => busy; private set { Set(ref busy, value); OcrCommand.Refresh(); TranslateCommand.Refresh(); DownloadCommand.Refresh(); CancelCommand.Refresh(); } }
    public double Progress { get => progress; private set => Set(ref progress, value); }
    public AsyncCommand OcrCommand { get; }
    public AsyncCommand TranslateCommand { get; }
    public AsyncCommand DownloadCommand { get; }
    public RelayCommand CopySourceCommand { get; }
    public RelayCommand CopyTranslationCommand { get; }
    public RelayCommand CancelCommand { get; }
    private async Task Run(Func<CancellationToken, Task> action)
    {
        if (Busy) return; operation = new CancellationTokenSource(); Busy = true;
        try { await action(operation.Token); }
        catch (OperationCanceledException) { Status = "操作已取消，已有结果保留。"; }
        finally { Busy = false; operation.Dispose(); operation = null; }
    }
    private void Report(Exception e) => Status = "操作失败：" + e.Message;
    private void Copy(string value) { try { if (!string.IsNullOrEmpty(value)) Clipboard.SetText(value); } catch (Exception e) { Report(e); } }
    public void Dispose() => operation?.Cancel();
}
