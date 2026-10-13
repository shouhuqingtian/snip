using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Snip.App.Mvvm;
using Snip.Translation;
using Snip.Core;

namespace Snip.App.ViewModels;

public sealed class TextResultViewModel : ObservableObject, IDisposable
{
    private readonly BitmapSource snapshot;
    private ImagePayload? image;
    private readonly IOcrService ocr;
    private readonly ModelStore models;
    private readonly ITranslationService translator;
    private CancellationTokenSource? operation;
    private readonly DiagnosticJournal? diagnostics;
    private DiagnosticOperation? diagnosticOperation;
    private readonly DispatcherTimer heartbeat = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Stopwatch elapsed = new();
    private string stage = "", source = "", translated = "", status = "正在准备文字识别…", direction = "en-zh";
    private string? language;
    private IReadOnlyList<string> languages = [];
    private bool busy, downloading, disposed, initialized, userCancelled;
    private bool autoTranslate, showTranslation;
    private double progress;
    public TextResultViewModel(BitmapSource image, IOcrService ocr, ModelStore models, ITranslationService translator, DiagnosticJournal? diagnostics = null)
    {
        snapshot = image.IsFrozen ? image : image.Clone(); snapshot.Freeze();
        this.ocr = ocr; this.models = models; this.translator = translator;
        this.diagnostics = diagnostics;
        // No codec, WinRT or model calls in the constructor: first paint must not wait on native work.
        heartbeat.Tick += (_, _) => ShowStage();
        OcrCommand = new(() => Run("文字识别", TimeSpan.FromSeconds(45), Recognize), Report, () => !Busy);
        TranslateCommand = new(TranslateAsync, Report, () => !Busy && !string.IsNullOrWhiteSpace(Source));
        DownloadCommand = new(() => Run("下载模型", TimeSpan.FromMinutes(15), async token =>
        {
            downloading = true; Notify(nameof(Indeterminate));
            int loggedPercent = -1;
            var updates = new Progress<DownloadProgress>(p =>
            {
                if (disposed || operation is null || operation.Token != token || token.IsCancellationRequested) return;
                Progress = p.CompletedBytes * 100.0 / p.TotalBytes;
                int bucket = (int)Progress / 10;
                if (bucket != loggedPercent) { loggedPercent = bucket; diagnosticOperation?.Stage("model-download-progress", new { p.CompletedBytes, p.TotalBytes }); }
                SetStage($"下载模型 {p.Direction}：{Progress:F1}%（共 {p.TotalBytes / 1024 / 1024} MB）");
            });
            await Task.Run(() => models.InstallAsync(updates, token), token);
            token.ThrowIfCancellationRequested(); Status = "两个方向的模型已校验，可断网翻译。";
        }), Report, () => !Busy);
        CopySourceCommand = new(() => Copy(Source)); CopyTranslationCommand = new(() => Copy(Translated));
        CancelCommand = new(() => { userCancelled = true; if (operation is { } current) _ = current.CancelAsync(); }, () => Busy);
    }
    public Task InitializeAsync()
    {
        if (initialized) return Task.CompletedTask;
        initialized = true;
        return Run("准备文字识别", TimeSpan.FromSeconds(45), Recognize);
    }
    public Task TranslateAsync() { ShowTranslation = true; return Run("离线翻译", TimeSpan.FromSeconds(90), Translate); }
    public IReadOnlyList<string> Languages { get => languages; private set => Set(ref languages, value); }
    public bool AutoTranslate { get => autoTranslate; init { autoTranslate = value; ShowTranslation = value; } }
    public BitmapSource PreviewImage => snapshot;
    public bool ShowTranslation { get => showTranslation; private set { if (Set(ref showTranslation, value)) Notify(nameof(WindowTitle)); } }
    public string WindowTitle => ShowTranslation ? "自用截图 · 文字识别与离线翻译" : "自用截图 · 文字识别";
    public string? Language { get => language; set { if (value is not null) Set(ref language, value); } }
    public string Direction { get => direction; set { if (value is "en-zh" or "zh-en") Set(ref direction, value); } }
    public string Source { get => source; set { if (Set(ref source, value)) TranslateCommand.Refresh(); } }
    public string Translated { get => translated; private set => Set(ref translated, value); }
    public string Status { get => status; private set => Set(ref status, value); }
    public bool Busy { get => busy; private set { Set(ref busy, value); Notify(nameof(Indeterminate)); OcrCommand.Refresh(); TranslateCommand.Refresh(); DownloadCommand.Refresh(); CancelCommand.Refresh(); } }
    public bool Indeterminate => Busy && !downloading;
    public double Progress { get => progress; private set => Set(ref progress, value); }
    public AsyncCommand OcrCommand { get; }
    public AsyncCommand TranslateCommand { get; }
    public AsyncCommand DownloadCommand { get; }
    public RelayCommand CopySourceCommand { get; }
    public RelayCommand CopyTranslationCommand { get; }
    public RelayCommand CancelCommand { get; }
    public void WindowOpened() => diagnostics?.Record("text-window", "loaded", new { AutoTranslate });
    public void WindowFailed(Exception error) => Report(error);
    private async Task Recognize(CancellationToken token)
    {
        if (Languages.Count == 0)
        {
            SetStage("正在加载 Windows 识别语言");
            diagnosticOperation?.Stage("ocr-languages-loading");
            var available = await Task.Run(() => ocr.Languages, token);
            token.ThrowIfCancellationRequested(); Languages = available;
            diagnosticOperation?.Stage("ocr-languages-loaded", new { Count = available.Count });
            if (Languages.Count == 0) throw new InvalidOperationException("未安装 Windows OCR 语言组件，请在 Windows 语言设置中添加中文/英语文字识别组件。");
            Language = Language is { } selected && Languages.Contains(selected) ? selected :
                Languages.FirstOrDefault(x => x.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) ?? Languages[0];
        }
        SetStage("正在识别文字");
        if (image is null)
        {
            diagnosticOperation?.Stage("png-encoding");
            var payload = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(snapshot));
                using var stream = new MemoryStream(); encoder.Save(stream);
                return new ImagePayload(stream.ToArray(), snapshot.PixelWidth, snapshot.PixelHeight);
            }, token);
            token.ThrowIfCancellationRequested(); image = payload;
        }
        var selectedLanguage = Language;
        diagnosticOperation?.Stage("ocr-recognizing", new { Language = selectedLanguage, image.Width, image.Height });
        var result = await Task.Run(() => ocr.RecognizeAsync(image, selectedLanguage, token), token);
        token.ThrowIfCancellationRequested();
        diagnosticOperation?.Stage("ocr-recognized", new { RegionCount = result.Regions.Count });
        Source = string.Join(Environment.NewLine, result.Regions.Select(r => r.Text));
        Status = string.IsNullOrWhiteSpace(Source) ? "没有识别到文字；可更换语言或重新截图。" : "文字已识别，可修正原文后翻译或复制。";
        if (AutoTranslate && !string.IsNullOrWhiteSpace(Source))
        {
            Direction = Source.Any(c => c >= 0x4e00 && c <= 0x9fff) ? "zh-en" : "en-zh";
            // Give the translation its own deadline rather than spending the OCR budget on both.
            if (operation is not null) operation.CancelAfter(TimeSpan.FromSeconds(90));
            await Translate(token);
        }
    }
    private async Task Translate(CancellationToken token)
    {
        SetStage("正在校验并加载翻译模型");
        var input = Source; var selectedDirection = Direction;
        diagnosticOperation?.Stage("translation-starting", new { Direction = selectedDirection, CharacterCount = input.Length });
        string? loggedStage = null; int loggedSegments = -1, loggedTokens = -1;
        var updates = new Progress<TranslationProgress>(p =>
        {
            if (disposed || operation is null || operation.Token != token || token.IsCancellationRequested) return;
            if (loggedStage != p.Stage || loggedSegments != p.CompletedSegments || p.GeneratedTokens / 25 != loggedTokens)
            {
                loggedStage = p.Stage; loggedSegments = p.CompletedSegments; loggedTokens = p.GeneratedTokens / 25;
                diagnosticOperation?.Stage("translation-progress", new { EngineStage = p.Stage, p.CompletedSegments, p.TotalSegments, p.GeneratedTokens });
            }
            SetStage(p.TotalSegments == 0 ? p.Stage : $"{p.Stage}：已完成 {p.CompletedSegments}/{p.TotalSegments} 段");
        });
        var result = await Task.Run(() => translator.TranslateAsync(input, selectedDirection, updates, token), token);
        token.ThrowIfCancellationRequested(); Translated = result; Status = "离线翻译完成。";
    }
    private void SetStage(string value) { stage = value; ShowStage(); }
    private void ShowStage() { if (Busy) Status = $"{stage} · 已用 {elapsed.Elapsed.TotalSeconds:F0} 秒，可取消"; }
    private async Task Run(string name, TimeSpan timeout, Func<CancellationToken, Task> action)
    {
        if (Busy || disposed) return;
        using var current = new CancellationTokenSource(timeout); operation = current;
        using var log = diagnostics?.Begin(name == "离线翻译" ? "translation" : name == "下载模型" ? "model-download" : "ocr",
            new { snapshot.PixelWidth, snapshot.PixelHeight, TimeoutSeconds = timeout.TotalSeconds });
        diagnosticOperation = log;
        userCancelled = false; downloading = false; Progress = 0; Busy = true; elapsed.Restart(); SetStage(name); heartbeat.Start();
        try { await action(current.Token).WaitAsync(current.Token); log?.Complete(); }
        catch (OperationCanceledException)
        {
            log?.Cancel(userCancelled ? "user" : disposed ? "window-closed" : "timeout");
            Status = !userCancelled ? "操作超时，已有结果保留。请缩小截图或分段翻译后重试。" : "操作已取消，已有结果保留。";
        }
        catch (Exception e) { log?.Fail(e); Report(e); }
        finally { heartbeat.Stop(); elapsed.Stop(); operation = null; diagnosticOperation = null; Busy = false; }
    }
    private void Report(Exception e) { diagnostics?.Record("text-result", "operation-failed", error: e); Status = "操作失败：" + e.Message + "；可从主窗口导出诊断包。"; }
    private void Copy(string value) { try { if (!string.IsNullOrEmpty(value)) Clipboard.SetText(value); } catch (Exception e) { Report(e); } }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; diagnostics?.Record("text-window", "closed", new { Busy });
        heartbeat.Stop(); if (operation is { } current) _ = current.CancelAsync();
    }
}
