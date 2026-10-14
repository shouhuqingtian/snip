using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Snip.App.Mvvm;
using Snip.Translation;
using Snip.Core;
using Snip.Infrastructure;

namespace Snip.App.ViewModels;

public sealed class TextResultViewModel : ObservableObject, IDisposable
{
    private readonly BitmapSource snapshot;
    private ImagePayload? image;
    private readonly IOcrService ocr;
    private readonly ModelStore models;
    private readonly ITranslationService translator;
    private readonly ITranslationService? onlineTranslator;
    private bool useOnlineTranslation;
    private readonly ITextClipboardService clipboard;
    private CancellationTokenSource? copying;
    private string copyStatus = "";
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
    public TextResultViewModel(BitmapSource image, IOcrService ocr, ModelStore models, ITranslationService translator, DiagnosticJournal? diagnostics = null, ITextClipboardService? clipboard = null, ITranslationService? onlineTranslator = null, Action? configureOnline = null, bool defaultOnline = false)
    {
        snapshot = image.IsFrozen ? image : image.Clone(); snapshot.Freeze();
        this.ocr = ocr; this.models = models; this.translator = translator;
        this.onlineTranslator = onlineTranslator; useOnlineTranslation = defaultOnline && onlineTranslator is not null;
        this.diagnostics = diagnostics;
        this.clipboard = clipboard ?? new TextClipboardService();
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
            token.ThrowIfCancellationRequested(); Status = "Hy-MT2 中英互译模型和推理器已校验，可断网翻译。";
        }), Report, () => !Busy);
        CopySourceCommand = new(() => CopyAsync(Source, false), Report, () => !disposed && copying is null && Source.Length > 0);
        CopyTranslationCommand = new(() => CopyAsync(Translated, true), Report, () => !disposed && copying is null && Translated.Length > 0);
        ConfigureOnlineCommand = new(configureOnline ?? (() => { }), () => configureOnline is not null && !Busy);
        CancelCommand = new(() => { userCancelled = true; if (operation is { } current) _ = current.CancelAsync(); }, () => Busy);
    }
    public Task InitializeAsync()
    {
        if (initialized) return Task.CompletedTask;
        initialized = true;
        return Run("准备文字识别", TimeSpan.FromSeconds(45), Recognize);
    }
    public Task TranslateAsync() { ShowTranslation = true; return Run(UseOnlineTranslation ? "在线翻译" : "离线翻译", TimeSpan.FromSeconds(90), Translate); }
    public bool UseOnlineTranslation
    {
        get => useOnlineTranslation;
        set { if (!Busy && Set(ref useOnlineTranslation,value)) { Notify(nameof(TranslationActionText)); Notify(nameof(TranslationNotice)); Notify(nameof(WindowTitle)); } }
    }
    public bool CanChangeTranslationMode => !Busy;
    public string TranslationActionText => UseOnlineTranslation ? "在线翻译" : "离线翻译";
    public string TranslationNotice => UseOnlineTranslation ? "原文将发送给所配置的大模型服务，按服务商规则计费。" : "Hy-MT2 专业中英翻译，文字留在本机。";
    public RelayCommand ConfigureOnlineCommand { get; }
    public IReadOnlyList<string> Languages { get => languages; private set => Set(ref languages, value); }
    public bool AutoTranslate { get => autoTranslate; init { autoTranslate = value; ShowTranslation = value; } }
    public BitmapSource PreviewImage => snapshot;
    public bool ShowTranslation { get => showTranslation; private set { if (Set(ref showTranslation, value)) Notify(nameof(WindowTitle)); } }
    public string WindowTitle => ShowTranslation ? UseOnlineTranslation ? "自用截图 · 文字识别与在线翻译" : "自用截图 · 文字识别与离线翻译" : "自用截图 · 文字识别";
    public string? Language { get => language; set { if (value is not null) Set(ref language, value); } }
    public string Direction { get => direction; set { if (value is "en-zh" or "zh-en") Set(ref direction, value); } }
    public string Source { get => source; set { if (Set(ref source, value)) { TranslateCommand.Refresh(); CopySourceCommand.Refresh(); } } }
    public string Translated { get => translated; private set { if (Set(ref translated, value)) CopyTranslationCommand.Refresh(); } }
    public string Status { get => status; private set => Set(ref status, value); }
    public string CopyStatus { get => copyStatus; private set => Set(ref copyStatus, value); }
    public bool Busy { get => busy; private set { Set(ref busy, value); Notify(nameof(Indeterminate)); Notify(nameof(CanChangeTranslationMode)); OcrCommand.Refresh(); TranslateCommand.Refresh(); DownloadCommand.Refresh(); CancelCommand.Refresh(); ConfigureOnlineCommand.Refresh(); } }
    public bool Indeterminate => Busy && !downloading;
    public double Progress { get => progress; private set => Set(ref progress, value); }
    public AsyncCommand OcrCommand { get; }
    public AsyncCommand TranslateCommand { get; }
    public AsyncCommand DownloadCommand { get; }
    public AsyncCommand CopySourceCommand { get; }
    public AsyncCommand CopyTranslationCommand { get; }
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
            token.ThrowIfCancellationRequested(); Languages = available.Count == 0 ? [] : new[] { OcrLanguageSelection.Automatic }.Concat(available).ToArray();
            diagnosticOperation?.Stage("ocr-languages-loaded", new { Count = available.Count });
            if (Languages.Count == 0) throw new InvalidOperationException("未安装 Windows OCR 语言组件，请在 Windows 语言设置中添加中文/英语文字识别组件。");
            Language = Language is { } selected && Languages.Contains(selected) ? selected : OcrLanguageSelection.Automatic;
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
        var installed = Languages.Where(value => value != OcrLanguageSelection.Automatic).ToArray();
        var candidates = OcrLanguageSelection.Candidates(installed, selectedLanguage);
        if (candidates.Count == 0) throw new InvalidOperationException("所选 Windows OCR 语言组件不可用，请切换到自动模式或安装相应语言组件。");
        OcrResult? result = null;
        string? recognizedLanguage = null;
        foreach (var candidate in candidates)
        {
            token.ThrowIfCancellationRequested(); SetStage("正在识别文字（" + candidate + "）");
            diagnosticOperation?.Stage("ocr-recognizing", new { Language = candidate, Automatic = selectedLanguage == OcrLanguageSelection.Automatic, image.Width, image.Height });
            result = await Task.Run(() => ocr.RecognizeAsync(image, candidate, token), token);
            token.ThrowIfCancellationRequested();
            diagnosticOperation?.Stage("ocr-recognized", new { Language = candidate, RegionCount = result.Regions.Count });
            if (result.Regions.Any(region => !string.IsNullOrWhiteSpace(region.Text))) { recognizedLanguage = candidate; break; }
        }
        Source = string.Join(Environment.NewLine, result!.Regions.Select(r => r.Text));
        if (string.IsNullOrWhiteSpace(Source))
            Status = !installed.Any(value => value.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                ? "没有识别到文字。英文截图需要 Windows 英语 OCR 组件；请在 Windows 语言设置中添加英语及文字识别组件后重试。"
                : selectedLanguage != OcrLanguageSelection.Automatic
                    ? "没有识别到文字；请切换到“自动（中英）”或与截图内容匹配的语言后重新识别，也可扩大截图边缘留白。"
                    : "没有识别到文字；已尝试已安装的中英识别组件。请截取清晰文字并保留少量边缘留白。";
        else Status = "文字已识别（" + recognizedLanguage + "），可修正原文后翻译或复制。";
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
        bool online = UseOnlineTranslation;
        var service = online ? onlineTranslator ?? throw new InvalidOperationException("请先配置在线翻译服务。") : translator;
        SetStage(online ? "正在连接在线翻译服务" : "正在校验并加载翻译模型");
        var input = Source; var selectedDirection = Direction;
        diagnosticOperation?.Stage("translation-starting", new { Direction = selectedDirection, CharacterCount = input.Length, Online = online });
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
        var result = await Task.Run(() => service.TranslateAsync(input, selectedDirection, updates, token), token);
        token.ThrowIfCancellationRequested(); Translated = result; Status = online ? "在线翻译完成。" : "离线翻译完成。";
    }
    private void SetStage(string value) { stage = value; ShowStage(); }
    private void ShowStage() { if (Busy) Status = $"{stage} · 已用 {elapsed.Elapsed.TotalSeconds:F0} 秒，可取消"; }
    private async Task Run(string name, TimeSpan timeout, Func<CancellationToken, Task> action)
    {
        if (Busy || disposed) return;
        using var current = new CancellationTokenSource(timeout); operation = current;
        using var log = diagnostics?.Begin(name is "离线翻译" or "在线翻译" ? "translation" : name == "下载模型" ? "model-download" : "ocr",
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
    private async Task CopyAsync(string value, bool translation)
    {
        if (disposed || copying is not null || value.Length == 0) return;
        using var current = new CancellationTokenSource(); copying = current;
        using var log = diagnostics?.Begin("text-clipboard", new { Translation = translation, CharacterCount = value.Length });
        CopySourceCommand.Refresh(); CopyTranslationCommand.Refresh();
        CopyStatus = "正在复制…";
        var retries = new Progress<int>(attempt =>
        {
            if (disposed || copying != current || current.IsCancellationRequested) return;
            log?.Stage("clipboard-busy", new { Attempt = attempt });
            CopyStatus = "剪贴板暂时被占用，正在重试…";
        });
        try
        {
            await clipboard.CopyAsync(value, current.Token, retries);
            if (disposed || current.IsCancellationRequested) { log?.Cancel("window-closed"); return; }
            log?.Complete(); CopyStatus = translation ? "译文已复制到剪贴板。" : "文字已复制到剪贴板。";
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested) { log?.Cancel("window-closed"); }
        catch (Exception error)
        {
            log?.Fail(error);
            if (!disposed) CopyStatus = error.HResult == TextClipboardService.ClipboardBusy
                ? "剪贴板持续被其他程序占用，复制未完成；请稍后重试。文字已保留，也可选中文字后按 Ctrl+C。"
                : "复制失败，文字已保留；可选中文字后按 Ctrl+C，或导出诊断包反馈。";
        }
        finally { copying = null; CopySourceCommand.Refresh(); CopyTranslationCommand.Refresh(); }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; diagnostics?.Record("text-window", "closed", new { Busy });
        heartbeat.Stop(); if (operation is { } current) _ = current.CancelAsync();
        copying?.Cancel(); CopySourceCommand.Refresh(); CopyTranslationCommand.Refresh();
    }
}
