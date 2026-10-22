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
    private readonly IOcrPreferencesStore? ocrPreferences;
    private readonly ModelStore models;
    private readonly ITranslationService translator;
    private readonly ITranslationService? onlineTranslator;
    private TranslationMode mode;
    private TranslationMode? translatedMode;
    private readonly ITranslationPreferencesStore? preferences;
    private readonly ITranslationService basicTranslator;
    private bool runtimeHelpRequested;
    private readonly ITextClipboardService clipboard;
    private readonly ITextSpeechService speech;
    private readonly Action? voiceModeSaved;
    private readonly ITextReadingPreferencesStore? readingPreferences;
    private bool? savedSourceExpanded;
    private bool speakingOriginal;
    private SpeechMode voiceMode;
    private string readingLayoutStatus = "展开或收起原文时自动保存，新截图和重启后沿用。";
    private CancellationTokenSource? speechOperation;
    private int speechGeneration;
    private bool speaking, sourceExpanded = true, showOptions;
    private int resultTab;
    private string speechStatus = "";
    private CancellationTokenSource? copying;
    private string copyStatus = "";
    private CancellationTokenSource? operation;
    private readonly DiagnosticJournal? diagnostics;
    private DiagnosticOperation? diagnosticOperation;
    private readonly DispatcherTimer heartbeat = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Stopwatch elapsed = new();
    private string stage = "", source = "", translated = "", status = "正在准备文字识别…", direction = "auto";
    private string languageNotes = "", resultDirection = "", vocabularySpeechText = "";
    private string? language;
    private IReadOnlyList<string> languages = [];
    private bool busy, downloading, disposed, initialized, userCancelled;
    private bool autoTranslate, showTranslation;
    private bool hasSourceLayout;
    private int sourceCharacterCount;
    private long sourceRevision;
    private double progress;
    private TranslationComputeMode computeMode;
    private string computeStatus = "本地计算：尚未运行";
    public TextResultViewModel(BitmapSource image, IOcrService ocr, ModelStore models, ITranslationService translator, DiagnosticJournal? diagnostics = null, ITextClipboardService? clipboard = null, ITranslationService? onlineTranslator = null, Action? configureOnline = null, bool defaultOnline = false, ITranslationPreferencesStore? preferences = null, TranslationPreferences? initialPreferences = null, ITranslationService? basicTranslator = null, ITextSpeechService? speech = null, IOcrPreferencesStore? ocrPreferences = null, ITextReadingPreferencesStore? readingPreferences = null, Action? configureSpeech = null, Action? voiceModeSaved = null)
    {
        snapshot = image.IsFrozen ? image : image.Clone(); snapshot.Freeze();
        this.ocr = ocr; this.ocrPreferences = ocrPreferences; this.models = models; this.translator = translator;
        this.onlineTranslator = onlineTranslator; this.preferences = preferences;
        this.basicTranslator = basicTranslator ?? new BasicDictionaryTranslator();
        mode = initialPreferences?.Mode ?? (defaultOnline && onlineTranslator is not null ? TranslationMode.Online : TranslationMode.Offline);
        direction = initialPreferences?.Direction ?? "auto";
        this.diagnostics = diagnostics;
        try { computeMode = models.ComputeSettings.Read(); }
        catch (InvalidOperationException error) { computeMode = TranslationComputeMode.Auto; diagnostics?.Record("translation-compute", "invalid-configuration", error: error); }
        this.clipboard = clipboard ?? new TextClipboardService();
        this.speech = speech ?? new WindowsTextSpeechService();
        this.voiceModeSaved = voiceModeSaved;
        this.readingPreferences = readingPreferences;
        try { savedSourceExpanded = readingPreferences?.ReadSourceExpanded(); sourceExpanded = savedSourceExpanded ?? true; }
        catch (InvalidOperationException error)
        { diagnostics?.Record("text-reading-preferences", "read-failed", error: error); readingLayoutStatus = "原文展开设置读取失败；重新展开或收起可保存。"; }
        // No codec, WinRT or model calls in the constructor: first paint must not wait on native work.
        heartbeat.Tick += (_, _) => ShowStage();
        OcrCommand = new(() => Run("文字识别", TimeSpan.FromSeconds(75), Recognize), Report, () => !Busy);
        DownloadOcrCommand = new(() => Run("下载增强 OCR", TimeSpan.FromMinutes(15), async token =>
        {
            if (ocr is not IOcrEnhancement enhancement) return;
            downloading = true; Notify(nameof(Indeterminate)); ShowOptions = false;
            var updates = new Progress<OcrDownloadProgress>(p =>
            {
                if (disposed || operation is null || operation.Token != token || token.IsCancellationRequested) return;
                Progress = p.CompletedBytes * 100.0 / p.TotalBytes;
                SetStage($"下载增强 OCR：{Progress:F1}%（约 31 MB，截图不会上传）");
            });
            await Task.Run(() => enhancement.InstallAsync(updates, token), token);
            token.ThrowIfCancellationRequested(); Languages = [];
            if (ocr is IOcrModeSelection selection) selection.Mode = OcrMode.EnhancedOffline;
            NotifyOcrSettings(); downloading = false; Notify(nameof(Indeterminate));
            operation?.CancelAfter(TimeSpan.FromSeconds(45));
            await Recognize(token);
        }), Report, () => !Busy && SupportsEnhancedOcr);
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
        }), Report, () => CanUseLocalCompute);
        DownloadGpuCommand = new(() => Run("下载 GPU 运行库", TimeSpan.FromMinutes(5), async token =>
        {
            downloading = true; Notify(nameof(Indeterminate));
            int loggedPercent = -1;
            var updates = new Progress<DownloadProgress>(p =>
            {
                if (disposed || operation is null || operation.Token != token || token.IsCancellationRequested) return;
                Progress = p.CompletedBytes * 100.0 / p.TotalBytes;
                int bucket = (int)Progress / 10;
                if (bucket != loggedPercent) { loggedPercent = bucket; diagnosticOperation?.Stage("gpu-download-progress", new { p.CompletedBytes, p.TotalBytes }); }
                SetStage($"下载 GPU 运行库：{Progress:F1}%（约 {p.TotalBytes / 1000000} MB）");
            });
            await Task.Run(() => models.InstallGpuRuntimeAsync(updates, token), token);
            token.ThrowIfCancellationRequested(); ComputeStatus = "GPU 运行库已校验；下次离线翻译时检测兼容 GPU。";
            Status = "GPU 运行库已准备好，复用现有翻译模型；自动 / 优先 GPU 模式下可尝试加速。";
        }), Report, () => CanUseLocalCompute);
        CopySourceCommand = new(() => CopyAsync(Source, false), Report, () => !disposed && copying is null && Source.Length > 0);
        CopyTranslationCommand = new(() => CopyAsync(Translated, true), Report, () => !disposed && copying is null && Translated.Length > 0);
        ConfigureOnlineCommand = new(configureOnline ?? (() => { }), () => configureOnline is not null && !Busy);
        ConfigureSpeechCommand = new(configureSpeech ?? (() => { }), () => configureSpeech is not null && !Busy);
        SaveTranslationSettingsCommand = new(SaveTranslationSettings, () => !Busy && preferences is not null);
        SaveOcrSettingsCommand = new(SaveOcrSettings, () => !Busy && SupportsOcrModes && ocrPreferences is not null);
        ToggleSourceCommand = new(ToggleSource, () => !disposed && ShowTranslation);
        ToggleOptionsCommand = new(() => ShowOptions = !ShowOptions);
        SpeakSourceCommand = new(() => _ = ToggleSpeechAsync(original: true), () => !disposed && (Speaking && speakingOriginal || !Busy && ShowSourceSpeechButton));
        SpeakTranslationCommand = new(() => _ = ToggleSpeechAsync(), () => !disposed && (Speaking && !speakingOriginal || !Busy && ShowSpeechButton));
        CancelCommand = new(() => { userCancelled = true; if (operation is { } current) _ = current.CancelAsync(); }, () => Busy);
        ReloadVoicePreferences(stopPlayback: false);
    }
    public Task InitializeAsync()
    {
        if (initialized) return Task.CompletedTask;
        initialized = true;
        return Run("准备文字识别", TimeSpan.FromSeconds(75), Recognize);
    }
    public Task TranslateAsync() { ShowTranslation = true; return Run(TranslationActionText, TimeSpan.FromSeconds(90), Translate); }
    public TranslationMode Mode
    {
        get => mode;
        set
        {
            if (Busy || !Enum.IsDefined(value) || !Set(ref mode,value)) return;
            Notify(nameof(UseOnlineTranslation));Notify(nameof(TranslationActionText));Notify(nameof(TranslationNotice));Notify(nameof(TranslationResultTitle));
            Notify(nameof(WindowTitle));Notify(nameof(ShowLocalCompute));Notify(nameof(CanUseLocalCompute));Notify(nameof(NeedsWindowsRuntime));DownloadCommand.Refresh();DownloadGpuCommand.Refresh();
        }
    }
    // Kept for callers that select the previous online/offline toggle.
    public bool UseOnlineTranslation { get => Mode == TranslationMode.Online; set => Mode = value ? TranslationMode.Online : TranslationMode.Offline; }
    public bool CanChangeTranslationMode => !Busy;
    public bool CanUseLocalCompute => !Busy && Mode == TranslationMode.Offline;
    public RelayCommand SaveTranslationSettingsCommand { get; }
    private void SaveTranslationSettings()
    {
        try { preferences!.Save(new(Mode, Direction));Status = "已保存默认翻译方式和方向，下次打开及重启软件后沿用；不会自动请求服务器或加载模型。"; }
        catch(Exception error) { diagnostics?.Record("translation-preferences","save-failed",error:error);Status = "默认翻译设置保存失败，原设置保留；请检查设置目录权限。"; }
    }
    public TranslationComputeMode ComputeMode
    {
        get => computeMode;
        set
        {
            if (!CanUseLocalCompute || computeMode == value || !Enum.IsDefined(value)) return;
            try
            {
                models.ComputeSettings.Save(value); Set(ref computeMode, value);
                ComputeStatus = value == TranslationComputeMode.Cpu ? "已保存：仅 CPU" : value == TranslationComputeMode.Gpu ? "已保存：优先 GPU，不可用时回退 CPU" : "已保存：自动选择 GPU / CPU";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            { diagnostics?.Record("translation-compute", "save-failed", error: error); ComputeStatus = "计算方式保存失败，原设置保留；请检查设置目录权限。"; Notify(); }
        }
    }
    public string ComputeStatus { get => computeStatus; private set => Set(ref computeStatus, value); }
    public bool NeedsWindowsRuntime => Mode == TranslationMode.Offline && (ModelStore.NeedsWindowsRuntime || runtimeHelpRequested);
    public string TranslationActionText => Mode == TranslationMode.Basic ? "基础查词" : UseOnlineTranslation ? "在线翻译" : "离线翻译";
    public string TranslationResultTitle => (translatedMode ?? Mode) == TranslationMode.Basic ? "基础查词结果" : "翻译结果";
    public string TranslationNotice => Mode == TranslationMode.Basic ? "10 万词条离线词典；查词及短语，不作整句翻译。" : UseOnlineTranslation ? "原文将发送给所配置的大模型服务，按服务商规则计费。" : "Hy-MT2 专业中英翻译，文字留在本机。";
    public RelayCommand ConfigureOnlineCommand { get; }
    public bool SupportsOcrModes => ocr is IOcrModeSelection;
    public bool CanChangeOcrMode => !Busy && SupportsOcrModes;
    public OcrMode RecognitionMode
    {
        get => ocr is IOcrModeSelection selection ? selection.Mode : UseEnhancedOcr ? OcrMode.EnhancedOffline : OcrMode.System;
        set
        {
            if (!CanChangeOcrMode || !Enum.IsDefined(value) || ocr is not IOcrModeSelection selection || selection.Mode == value) return;
            selection.Mode = value; Languages = []; Language = OcrLanguageSelection.Automatic; NotifyOcrSettings();
            Status = "已切换识别方式，点击重新识别生效；需要以后沿用时点击“保存识别默认”。";
        }
    }
    public string OcrModeNotice => RecognitionMode == OcrMode.Online
        ? "在线 OCR 会将当前截图发送给在线设置中的服务；共用密钥，可单独填写视觉模型。切换后点击重新识别。"
        : RecognitionMode == OcrMode.EnhancedOffline ? "增强离线 OCR：截图留在本机；首次需下载约 31 MB 模型，识别结束即释放。"
        : "系统 OCR：免下载模型，不上传截图；使用 Windows 已安装的识别语言。";
    public RelayCommand SaveOcrSettingsCommand { get; }
    private void SaveOcrSettings()
    {
        try { ocrPreferences!.Save(RecognitionMode); Status = "已保存默认识别方式，新截图和重启软件后沿用；不改变翻译默认设置。"; }
        catch (Exception error) { diagnostics?.Record("ocr-preferences", "save-failed", error: error); Status = "默认识别方式保存失败，原设置保留。"; }
    }
    public bool SupportsEnhancedOcr => ocr is IOcrEnhancement;
    public bool CanSwitchOcr => !Busy && ocr is IOcrEnhancement { IsInstalled: true };
    public bool CanSelectOcrLanguage => !Busy && RecognitionMode == OcrMode.System;
    public bool UseEnhancedOcr
    {
        get => ocr is IOcrEnhancement { IsInstalled: true, Enabled: true };
        set
        {
            if (!CanSwitchOcr || ocr is not IOcrEnhancement enhancement || enhancement.Enabled == value) return;
            try
            {
                enhancement.Enabled = value; Languages = []; NotifyOcrSettings();
                Status = value ? "已保存：增强离线 OCR；点击重新识别生效。" : "已保存：系统 OCR（免下载）；点击重新识别生效。";
            }
            catch (Exception error) { diagnostics?.Record("ocr", "settings-save-failed", error: error); Status = "识别方式保存失败，原设置保留。"; NotifyOcrSettings(); }
        }
    }
    public string OcrModelStatus => ocr is IOcrEnhancement { IsInstalled: true }
        ? "增强模型已安装。仅识别时启动，结束即退出；支持中英混排。"
        : "增强识别约需下载 31 MB 模型；保留免下载的系统 OCR。";
    private void NotifyOcrSettings()
    { Notify(nameof(RecognitionMode)); Notify(nameof(OcrModeNotice)); Notify(nameof(UseEnhancedOcr)); Notify(nameof(CanSwitchOcr)); Notify(nameof(CanSelectOcrLanguage)); Notify(nameof(OcrModelStatus)); }
    public AsyncCommand DownloadOcrCommand { get; }
    public IReadOnlyList<string> Languages { get => languages; private set => Set(ref languages, value); }
    public bool AutoTranslate { get => autoTranslate; init { autoTranslate = value; ShowTranslation = value; } }
    public BitmapSource PreviewImage => snapshot;
    public bool ShowTranslation { get => showTranslation; private set { if (Set(ref showTranslation, value)) { Notify(nameof(SourceExpanded)); Notify(nameof(SourceToggleText)); Notify(nameof(WindowTitle)); ToggleSourceCommand.Refresh(); } } }
    public string WindowTitle => ShowTranslation ? Mode == TranslationMode.Basic ? "自用截图 · 文字识别与基础词典" : UseOnlineTranslation ? "自用截图 · 文字识别与在线翻译" : "自用截图 · 文字识别与离线翻译" : "自用截图 · 文字识别";
    public string? Language { get => language; set { if (value is not null) Set(ref language, value); } }
    public string Direction { get => direction; set { if (!Busy && (value is "auto" or "en-zh" or "zh-en") && Set(ref direction, value)) Notify(nameof(DirectionHint)); } }
    public string DirectionHint => Direction == "auto" && string.IsNullOrWhiteSpace(Source) ? "自动：中文译英文，英文译中文" :
        (Direction == "auto" ? "自动判断：" : "手动指定：") + (TranslationDirection.Resolve(Source, Direction) == "zh-en" ? "中文 → 英文" : "英文 → 中文");
    public string ResultDirection { get => resultDirection; private set => Set(ref resultDirection, value); }
    public string LanguageNotes { get => languageNotes; private set { if (Set(ref languageNotes, value)) { Notify(nameof(HasLanguageNotes)); if (value.Length == 0) ResultTab = 0; } } }
    public bool SourceExpanded
    {
        get => !ShowTranslation || sourceExpanded;
        set { if (Set(ref sourceExpanded, value)) Notify(nameof(SourceToggleText)); }
    }
    public string ReadingLayoutStatus { get => readingLayoutStatus; private set => Set(ref readingLayoutStatus, value); }
    private void ToggleSource()
    {
        SourceExpanded = !SourceExpanded;
        savedSourceExpanded = sourceExpanded;
        try { readingPreferences?.SaveSourceExpanded(sourceExpanded); ReadingLayoutStatus = "已记住：原文" + (sourceExpanded ? "展开" : "收起") + "，新截图和重启后沿用。"; }
        catch (Exception error)
        { diagnostics?.Record("text-reading-preferences", "save-failed", error: error); ReadingLayoutStatus = "原文展开状态保存失败；本窗口仍可使用，请检查设置目录权限。"; Status = ReadingLayoutStatus; }
    }
    public string SourceToggleText => SourceExpanded ? "收起原文" : "展开原文";
    public bool ShowOptions { get => showOptions; set => Set(ref showOptions, value); }
    public int ResultTab
    {
        get => resultTab;
        set
        {
            if (value is not (0 or 1) || !Set(ref resultTab, value)) return;
            if (Speaking && !speakingOriginal) StopSpeech(clearStatus: true);
            RefreshSpeechActions();
        }
    }
    public bool ShowLocalCompute => Mode == TranslationMode.Offline;
    public string SourceSummary => "识别文字 · " + sourceCharacterCount + " 字";
    public bool SupportsSpeechModes => speech is IConfigurableTextSpeechService;
    public bool CanChangeVoiceMode => SupportsSpeechModes && !Busy;
    public RelayCommand ConfigureSpeechCommand { get; }
    public SpeechMode VoiceMode
    {
        get => voiceMode;
        set
        {
            if (!CanChangeVoiceMode || !Enum.IsDefined(value) || value == voiceMode) return;
            StopSpeech(clearStatus: true);
            try { ((IConfigurableTextSpeechService)speech).Mode = value; Set(ref voiceMode, value); voiceModeSaved?.Invoke(); SpeechStatus = value == SpeechMode.Online ? "已保存默认：在线 AI 语音；点击喇叭时发送对应文字。" : "已保存默认：Windows 系统语音。"; }
            catch (Exception error) { diagnostics?.Record("speech-settings", "save-failed", error: error); SpeechStatus = "语音方式保存失败，请先打开 AI 语音设置保存服务和密钥。"; Notify(); }
        }
    }
    public void ReloadVoicePreferences(bool stopPlayback = true)
    {
        if (stopPlayback) StopSpeech(clearStatus: true);
        try { if (speech is IConfigurableTextSpeechService options) { voiceMode = options.Mode; Notify(nameof(VoiceMode)); } }
        catch (Exception error) { diagnostics?.Record("speech-settings", "read-failed", error: error); SpeechStatus = "语音设置无法读取，请在 AI 语音设置中重新保存。"; }
    }
    public string SpeechStatus { get => speechStatus; private set => Set(ref speechStatus, value); }
    public bool Speaking { get => speaking; private set { if (Set(ref speaking, value)) RefreshSpeechActions(); } }
    public string SpeechActionText => Speaking && !speakingOriginal ? "停止朗读" : ResultTab == 1 ? "朗读词汇" : "朗读译文";
    public string SourceSpeechActionText => Speaking && speakingOriginal ? "停止朗读" : "朗读原文";
    private string ResultSpeechText => ResultTab == 1 ? vocabularySpeechText : Translated;
    public bool ShowSpeechButton => !string.IsNullOrWhiteSpace(ResultSpeechText);
    public bool ShowSourceSpeechButton => !string.IsNullOrWhiteSpace(Source);
    private void RefreshSpeechActions()
    {
        Notify(nameof(SpeechActionText)); Notify(nameof(SourceSpeechActionText));
        Notify(nameof(ShowSpeechButton)); Notify(nameof(ShowSourceSpeechButton));
        SpeakSourceCommand.Refresh(); SpeakTranslationCommand.Refresh();
    }
    public RelayCommand ToggleSourceCommand { get; }
    public RelayCommand ToggleOptionsCommand { get; }
    public RelayCommand SpeakSourceCommand { get; }
    public RelayCommand SpeakTranslationCommand { get; }
    public bool HasLanguageNotes => LanguageNotes.Length > 0;
    public string Source { get => source; set { if (Set(ref source, value)) { if (Speaking && speakingOriginal) StopSpeech(clearStatus: true); RefreshSpeechActions(); sourceRevision++; sourceCharacterCount = TextContent.CountCharacters(value); TranslateCommand.Refresh(); CopySourceCommand.Refresh(); Notify(nameof(DirectionHint)); Notify(nameof(SourceSummary)); } } }
    public bool HasTranslation => Translated.Length > 0;
    public bool HasSourceLayout { get => hasSourceLayout; private set => Set(ref hasSourceLayout, value); }
    public string Translated { get => translated; private set { if (Set(ref translated, value)) { CopyTranslationCommand.Refresh(); Notify(nameof(HasTranslation)); RefreshSpeechActions(); } } }
    public string Status { get => status; private set => Set(ref status, value); }
    public string CopyStatus { get => copyStatus; private set => Set(ref copyStatus, value); }
    public bool Busy { get => busy; private set { Set(ref busy, value); Notify(nameof(Indeterminate)); Notify(nameof(CanChangeTranslationMode)); Notify(nameof(CanUseLocalCompute)); Notify(nameof(CanSwitchOcr)); Notify(nameof(CanChangeOcrMode)); Notify(nameof(CanSelectOcrLanguage)); SaveOcrSettingsCommand.Refresh(); OcrCommand.Refresh(); DownloadOcrCommand.Refresh(); TranslateCommand.Refresh(); DownloadCommand.Refresh(); DownloadGpuCommand.Refresh(); CancelCommand.Refresh(); ConfigureOnlineCommand.Refresh(); ConfigureSpeechCommand.Refresh(); Notify(nameof(CanChangeVoiceMode)); SaveTranslationSettingsCommand.Refresh(); SpeakSourceCommand.Refresh(); SpeakTranslationCommand.Refresh(); } }
    public bool Indeterminate => Busy && !downloading;
    public double Progress { get => progress; private set => Set(ref progress, value); }
    public AsyncCommand OcrCommand { get; }
    public AsyncCommand TranslateCommand { get; }
    public AsyncCommand DownloadCommand { get; }
    public AsyncCommand DownloadGpuCommand { get; }
    public AsyncCommand CopySourceCommand { get; }
    public AsyncCommand CopyTranslationCommand { get; }
    public RelayCommand CancelCommand { get; }
    public void WindowOpened() => diagnostics?.Record("text-window", "loaded", new { AutoTranslate });
    public void WindowFailed(Exception error) => Report(error);
    private async Task Recognize(CancellationToken token)
    {
        SourceExpanded = savedSourceExpanded ?? true;
        if (Languages.Count == 0)
        {
            SetStage(RecognitionMode == OcrMode.System ? "正在加载 Windows 识别语言" : "正在准备中英自动识别");
            diagnosticOperation?.Stage("ocr-languages-loading");
            var available = await Task.Run(() => ocr.Languages, token);
            token.ThrowIfCancellationRequested(); Languages = available.Count == 0 ? [] : new[] { OcrLanguageSelection.Automatic }.Concat(available).ToArray();
            diagnosticOperation?.Stage("ocr-languages-loaded", new { Count = available.Count });
            if (Languages.Count == 0) throw new InvalidOperationException(SupportsEnhancedOcr
                ? "未安装 Windows OCR 语言组件。可在更多设置下载增强 OCR，或在 Windows 语言设置中添加中文/英语文字识别组件。"
                : "未安装 Windows OCR 语言组件，请在 Windows 语言设置中添加中文/英语文字识别组件。");
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
        IReadOnlyList<string> candidates = ocr is IOcrModeSelection { Mode: not OcrMode.System } || ocr is IOcrEnhancement { IsInstalled: true, Enabled: true }
            ? [OcrLanguageSelection.Automatic] : OcrLanguageSelection.Candidates(installed, selectedLanguage);
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
        HasSourceLayout = result!.LayoutText is not null;
        Source = result.LayoutText ?? string.Join(Environment.NewLine, result.Regions.Select(r => r.Text));
        if (string.IsNullOrWhiteSpace(Source))
            Status = RecognitionMode != OcrMode.System ? "没有识别到文字，请核对截图清晰度，或切换识别方式后重试。" : !installed.Any(value => value.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                ? "没有识别到文字。英文截图需要 Windows 英语 OCR 组件；请在 Windows 语言设置中添加英语及文字识别组件后重试。"
                : selectedLanguage != OcrLanguageSelection.Automatic
                    ? "没有识别到文字；请切换到“自动（中英）”或与截图内容匹配的语言后重新识别，也可扩大截图边缘留白。"
                    : "没有识别到文字；已尝试已安装的中英识别组件。请截取清晰文字并保留少量边缘留白。";
        else Status = ocr is IOcrEnhancement enhancement
            ? enhancement.EngineDescription + "，可修正后翻译或复制。" + (enhancement.IsInstalled || RecognitionMode == OcrMode.Online ? "" : "识别不准时，可在更多设置下载增强 OCR。")
            : "文字已识别（" + recognizedLanguage + "），可修正原文后翻译或复制。";
        if (AutoTranslate && !string.IsNullOrWhiteSpace(Source))
        {
            // Give the translation its own deadline rather than spending the OCR budget on both.
            if (operation is not null) operation.CancelAfter(TimeSpan.FromSeconds(90));
            await Translate(token);
        }
    }
    private async Task Translate(CancellationToken token)
    {
        long revision = sourceRevision;
        var input = HasSourceLayout ? TextContent.WithoutLayoutPadding(Source) : Source;
        var selectedDirection = TranslationDirection.Resolve(input, Direction);
        var selectedMode = Mode;
        bool online = selectedMode == TranslationMode.Online;
        bool basic = selectedMode == TranslationMode.Basic;
        ComputeStatus = basic ? "基础词典：本机查词，不加载模型" : online ? "在线翻译由服务商计算，本机 GPU 不启用" : "本地计算：正在准备";
        var service = basic ? basicTranslator : online ? onlineTranslator : translator;
        bool available = service is not null && await Task.Run(() => service.IsAvailableAsync(token), token);
        token.ThrowIfCancellationRequested();
        if (!TranslationSourceIsCurrent(revision)) return;
        if (!available)
        {
            if (Translated.Length == 0) ShowTranslation = false;
            if (!online) ComputeStatus = "本地计算：未加载翻译模型";
            diagnosticOperation?.Stage("ocr-only-fallback", new { Online = online, CharacterCount = Source.Length });
            Status = online ? "文字已识别，可修改和复制。在线翻译尚未配置，请先打开在线设置；OCR 不需要翻译模型。"
                : "文字已识别，可修改和复制。当前默认仅识别文字；需要翻译时可下载离线模型或配置在线服务。";
            return;
        }
        ShowTranslation = true;
        SetStage(basic ? "正在查询内置词典" : online ? "正在连接在线翻译服务" : "正在校验并加载翻译模型");
        diagnosticOperation?.Stage("translation-starting", new { Direction = selectedDirection, CharacterCount = input.Length, Online = online, Mode = Mode.ToString(), ComputeMode = ComputeMode.ToString() });
        string? cpuFallback = null;
        string? loggedStage = null; int loggedSegments = -1, loggedTokens = -1;
        var updates = new Progress<TranslationProgress>(p =>
        {
            if (disposed || operation is null || operation.Token != token || token.IsCancellationRequested) return;
            if (loggedStage != p.Stage || loggedSegments != p.CompletedSegments || p.GeneratedTokens / 25 != loggedTokens)
            {
                loggedStage = p.Stage; loggedSegments = p.CompletedSegments; loggedTokens = p.GeneratedTokens / 25;
                diagnosticOperation?.Stage("translation-progress", new { EngineStage = p.Stage, p.CompletedSegments, p.TotalSegments, p.GeneratedTokens });
            }
            if (p.Stage.Contains("回退 CPU", StringComparison.Ordinal) || p.Stage.Contains("运行库未安装", StringComparison.Ordinal)) cpuFallback = p.Stage;
            if (p.Stage.StartsWith("计算方式：", StringComparison.Ordinal)) ComputeStatus = p.Stage == "计算方式：CPU" && cpuFallback is not null ? p.Stage + " · " + cpuFallback : p.Stage;
            SetStage(p.TotalSegments == 0 ? p.Stage : $"{p.Stage}：已完成 {p.CompletedSegments}/{p.TotalSegments} 段");
        });
        AnnotatedTranslation result;
        if (online && service is IAnnotatedTranslationService annotated)
            result = await Task.Run(() => annotated.TranslateWithDetailsAsync(input, selectedDirection, updates, token), token);
        else
            result = new(await Task.Run(() => service!.TranslateAsync(input, selectedDirection, updates, token), token), "", []);
        token.ThrowIfCancellationRequested();
        if (disposed) return;
        if (!TranslationSourceIsCurrent(revision)) return;
        translatedMode = selectedMode; Notify(nameof(TranslationResultTitle));
        bool firstTranslation = Translated.Length == 0;
        // Keep structured vocabulary separate from display notes: never speak IPA, labels or meanings.
        vocabularySpeechText = online ? string.Join(". ", result.Words.Take(8)
            .Select(word => word.English.Trim()).Where(word => word.Length > 0)) : "";
        Translated = result.Translation;
        if (firstTranslation) SourceExpanded = savedSourceExpanded ?? false;
        ResultTab = 0;
        ResultDirection = selectedDirection == "zh-en" ? "本次：中文 → 英文" : "本次：英文 → 中文";
        LanguageNotes = online ? FormatNotes(result) : "";
        RefreshSpeechActions();
        CopyStatus = "";
        Status = basic ? "基础查词完成，未收录词语已标明；结果为词义参考。" : online ? "在线翻译完成。" + result.Notice : "离线翻译完成。";
    }
    private bool TranslationSourceIsCurrent(long revision)
    {
        if (revision == sourceRevision) return true;
        diagnosticOperation?.Stage("source-changed-result-ignored");
        Status = "原文已更改，本次旧原文的译文未应用；已有译文保留，请重新翻译。";
        return false;
    }
    private static string FormatNotes(AnnotatedTranslation result)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(result.Pinyin)) { lines.Add("中文译文拼音"); lines.Add(result.Pinyin); }
        if (result.Words.Count > 0)
        {
            if (lines.Count > 0) lines.Add("");
            lines.Add("英语词汇 · 美式音标 · 词性");
            foreach (var word in result.Words.Take(8))
                lines.Add(word.English + "  " + word.Ipa + "  " + word.PartOfSpeech + Environment.NewLine + word.Meaning);
        }
        if (lines.Count > 0) { lines.Add(""); lines.Add("注释由所选在线模型生成，可选中文字后按 Ctrl+C 复制。"); }
        return string.Join(Environment.NewLine, lines);
    }
    private void SetStage(string value) { stage = value; ShowStage(); }
    private void ShowStage() { if (Busy) Status = $"{stage} · 已用 {elapsed.Elapsed.TotalSeconds:F0} 秒，可取消"; }
    private async Task Run(string name, TimeSpan timeout, Func<CancellationToken, Task> action)
    {
        if (Busy || disposed) return;
        StopSpeech(clearStatus: true);
        using var current = new CancellationTokenSource(timeout); operation = current;
        using var log = diagnostics?.Begin(name is "离线翻译" or "在线翻译" or "基础查词" ? "translation" : name is "下载模型" or "下载 GPU 运行库" ? "model-download" : "ocr",
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
    private void Report(Exception e)
    {
        if (e.Message.Contains("Visual C++", StringComparison.Ordinal)) { runtimeHelpRequested = true; Notify(nameof(NeedsWindowsRuntime)); }
        diagnostics?.Record("text-result", "operation-failed", error: e); Status = "操作失败：" + e.Message + "；可从主窗口导出诊断包。";
    }
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
    public async Task ToggleSpeechAsync(bool original = false)
    {
        if (disposed) return;
        if (Speaking && speakingOriginal == original) { StopSpeech(); return; }
        if (Busy || !(original ? ShowSourceSpeechButton : ShowSpeechButton)) return;
        StopSpeech(clearStatus: true);
        using var current = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        speechOperation = current;
        int generation = ++speechGeneration;
        string text = original ? Source : ResultSpeechText;
        string contentName = original ? "原文" : ResultTab == 1 ? "词汇" : "译文";
        string speechLanguage = SpeechText.Language(text);
        speakingOriginal = original;
        Speaking = true; RefreshSpeechActions();
        SpeechStatus = "正在朗读" + contentName + (VoiceMode == SpeechMode.Online ? "（在线 AI 语音，将发送对应文字）" : "（Windows " + (speechLanguage == "zh" ? "中文" : "英语") + "语音）") + "；再次点击喇叭可停止。";
        using var log = diagnostics?.Begin("text-speech", new { CharacterCount = text.Length, Language = speechLanguage, Original = original, VoiceMode = VoiceMode.ToString() });
        try
        {
            await speech.SpeakAsync(text, speechLanguage, current.Token).WaitAsync(current.Token);
            current.Token.ThrowIfCancellationRequested();
            log?.Complete();
            if (!disposed && generation == speechGeneration) SpeechStatus = "朗读完成。";
        }
        catch (OperationCanceledException)
        {
            log?.Cancel("stopped-or-timeout");
            if (!disposed && generation == speechGeneration) SpeechStatus = "朗读已停止；长文可分段查看。";
        }
        catch (Exception error)
        {
            log?.Fail(error);
            if (!disposed && generation == speechGeneration)
                SpeechStatus = error is SpeechUnavailableException
                    ? "未安装 Windows " + (speechLanguage == "zh" ? "中文" : "英语") + "语音，请在系统“时间和语言 → 语音”中添加对应语音后重试。"
                    : error is SpeechServiceException ? error.Message : VoiceMode == SpeechMode.Online
                        ? "在线语音未完成，请检查 AI 语音配置和音频设备，或手动切回系统语音；文字已保留。"
                        : "无法朗读，请检查 Windows 语音组件和音频输出设备；原文和译文已保留。";
        }
        finally
        {
            if (speechOperation == current) { speechOperation = null; Speaking = false; }
        }
    }
    public void StopSpeechFor(bool original)
    { if (Speaking && speakingOriginal == original) StopSpeech(); }
    public void StopSpeech(bool clearStatus = false)
    {
        speechGeneration++;
        if (speechOperation is { } current) _ = current.CancelAsync();
        if (clearStatus) SpeechStatus = "";
        else if (!disposed) SpeechStatus = "朗读已停止。";
    }
    public void Dispose()
    {
        if (disposed) return;
        StopSpeech(clearStatus: true);
        disposed = true; SpeakSourceCommand.Refresh(); SpeakTranslationCommand.Refresh(); ToggleSourceCommand.Refresh(); diagnostics?.Record("text-window", "closed", new { Busy });
        heartbeat.Stop(); if (operation is { } current) _ = current.CancelAsync();
        copying?.Cancel(); CopySourceCommand.Refresh(); CopyTranslationCommand.Refresh();
    }
}
