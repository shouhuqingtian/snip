using System.Windows;
using System.Windows.Controls;
using Snip.App.ViewModels;
using Snip.App.Views;
using Snip.Core;
using Snip.Infrastructure;
using Snip.Translation;
using Snip.Ocr;
using System.Diagnostics;

namespace Snip.App.Services;

/// <summary>Composes optional desktop workflows without making editing depend on OCR or automation.</summary>
public sealed class FeatureHub
{
    private readonly AnnotationRenderer renderer;
    private readonly IImageExportService export;
    private readonly IDesktopCaptureService capture;
    private readonly PinManager pins;
    private readonly IOcrService ocr;
    private readonly IOcrService systemOcr = new OfflineOcrService();
    private readonly IOcrPreferencesStore ocrPreferences = new OcrPreferencesStore();
    private readonly ModelStore models;
    private readonly ITranslationService translator;
    private readonly IOnlineTranslationSettingsStore onlineSettings;
    private readonly ITranslationService onlineTranslator;
    private readonly ITranslationPreferencesStore translationPreferences;
    private readonly ISpeechSettingsStore speechSettings;
    private readonly ITextReadingPreferencesStore readingPreferences = new TextReadingPreferencesStore();
    private readonly DiagnosticJournal? diagnostics;
    private readonly Func<IntPtr, IWindowsShareSession>? shareFactory;
    private readonly Action<Exception>? captureFailure;
    public FeatureHub(AnnotationRenderer renderer, IImageExportService export, IDesktopCaptureService capture, PinManager pins, DiagnosticJournal? diagnostics = null,
        IOcrService? ocr = null, ITranslationService? translator = null, ModelStore? models = null, Func<IntPtr, IWindowsShareSession>? shareFactory = null, IOnlineTranslationSettingsStore? onlineSettings = null, ITranslationService? onlineTranslator = null, ITranslationPreferencesStore? translationPreferences = null, Action<Exception>? captureFailure = null)
    {
        this.captureFailure = captureFailure;
        this.renderer = renderer; this.export = export; this.capture = capture; this.pins = pins; this.diagnostics = diagnostics;
        this.ocr = ocr ?? new EnhancedOcrService(systemOcr, new OcrModelStore(System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SelfSnip", "Ocr", "PP-OCRv6-small")), WorkerHost, diagnostics);
        this.models = models ?? new(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SelfSnip", "Models"));
        this.translator = translator ?? new OfflineTranslator(this.models); this.shareFactory = shareFactory;
        if (this.translator is OfflineTranslator native)
            native.InferenceCompleted += metrics => diagnostics?.Record("translation", "native-released", new
            { Engine = ModelStore.Model.Name, metrics.ProcessId, metrics.PeakWorkingSetBytes, metrics.PeakCombinedWorkingSetBytes, metrics.ElapsedSeconds, metrics.ProcessExited, metrics.ExitCode, Backend = metrics.Backend.ToString(), metrics.OffloadedLayers, metrics.LayerSignalObserved, metrics.ProcessorSeconds, metrics.Threads, metrics.BatchThreads, metrics.Repack });
        this.translationPreferences = translationPreferences ?? new TranslationPreferencesStore();
        this.onlineSettings = onlineSettings ?? new OnlineTranslationSettingsStore();
        speechSettings = new OnlineSpeechSettingsStore(this.onlineSettings);
        this.onlineTranslator = onlineTranslator ?? new OnlineTranslator(() => this.onlineSettings.ReadCredentials());
    }
    internal static ProcessStartInfo WorkerHost()
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位截图程序。");
        // A library/test host must start Snip's worker entry, never recursively start itself.
        if (System.Reflection.Assembly.GetEntryAssembly() != typeof(App).Assembly)
        {
            string appHost = System.IO.Path.Combine(AppContext.BaseDirectory, "Snip.exe");
            if (!System.IO.File.Exists(appHost)) throw new InvalidOperationException("无法定位截图辅助程序。");
            return new ProcessStartInfo(appHost);
        }
        var info = new ProcessStartInfo(executable);
        if (System.IO.Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(System.IO.Path.Combine(AppContext.BaseDirectory, "Snip.dll"));
        return info;
    }
    public TextResultWindow? ShowText(EditorViewModel vm, bool translate)
    {
        using var operation = diagnostics?.Begin("text-window", new { Translate = translate });
        TextResultViewModel? text = null;
        TextResultWindow? window = null;
        try
        {
            var image = vm.Compose();
            operation?.Stage("image-composed", new { image.PixelWidth, image.PixelHeight });
            IOcrService selectedOcr = ocr;
            if (ocr is EnhancedOcrService enhanced)
            {
                OcrMode selected;
                try { selected = ocrPreferences.Read() ?? (enhanced.IsInstalled && enhanced.Enabled ? OcrMode.EnhancedOffline : OcrMode.System); }
                catch (InvalidOperationException error)
                { diagnostics?.Record("ocr-preferences", "invalid-configuration", error: error); selected = OcrMode.System; }
                selectedOcr = new SelectableOcrService(systemOcr, enhanced, new OnlineOcrService(() => onlineSettings.ReadCredentials()), selected);
            }
            text = new TextResultViewModel(image, selectedOcr, models, translator, diagnostics, onlineTranslator: onlineTranslator,
                configureOnline: () => ShowOnlineSettings(),
                defaultOnline: DefaultOnline(), preferences: translationPreferences, initialPreferences: ReadSavedTranslationPreferences(), ocrPreferences: ocrPreferences, readingPreferences: readingPreferences,
                speech: new ConfiguredTextSpeechService(speechSettings, new WindowsTextSpeechService(), credentials => new OnlineSpeechSynthesizer(credentials), new WindowsSpeechAudioPlayer()),
                configureSpeech: () => ShowSpeechSettings(), voiceModeSaved: RefreshVoiceModes) { AutoTranslate = translate };
            operation?.Stage("view-model-created");
            // A result must outlive the screenshot overlay. Do not give it the overlay as owner.
            window = new TextResultWindow(text) { WindowStartupLocation = WindowStartupLocation.CenterScreen };
            operation?.Stage("window-created");
            window.Show(); window.Activate();
            operation?.Complete(); return window;
        }
        catch (Exception error)
        {
            operation?.Fail(error); text?.Dispose(); window?.Close();
            MessageBox.Show("无法打开识别结果窗口：" + error.Message + "\n软件仍在运行，可从托盘导出诊断包。", "自用截图", MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }
    }
    private TranslationPreferences? ReadSavedTranslationPreferences()
    {
        try { return translationPreferences.Read(); }
        catch(InvalidOperationException error)
        {
            diagnostics?.Record("translation-preferences", "invalid-configuration", error:error);
            // A broken saved preference must not silently re-enable an online service.
            return new(TranslationMode.Offline);
        }
    }
    public TranslationPreferences DefaultTranslationPreferences() => ReadSavedTranslationPreferences() ?? new(DefaultOnline() ? TranslationMode.Online : TranslationMode.Offline);
    private bool DefaultOnline()
    {
        try { return onlineSettings.Read() is { DefaultOnline:true,HasApiKey:true }; }
        catch (InvalidOperationException error) { diagnostics?.Record("online-settings", "invalid-configuration", error: error); return false; }
    }
    public OnlineTranslationSettingsWindow ShowOnlineSettings(Action? saved = null)
    {
        var existing = Application.Current.Windows.OfType<OnlineTranslationSettingsWindow>().FirstOrDefault();
        if (existing is not null) { existing.Show(); existing.Activate(); return existing; }
        var vm = new OnlineTranslationSettingsViewModel(onlineSettings, configureSpeech: () => ShowSpeechSettings());
        if (saved is not null) vm.Saved += (_, _) => saved();
        var window = new OnlineTranslationSettingsWindow(vm); window.Show(); window.Activate(); return window;
    }
    private void RefreshVoiceModes()
    {
        foreach (var result in Application.Current.Windows.OfType<TextResultWindow>())
            if (result.DataContext is TextResultViewModel text) text.ReloadVoicePreferences();
    }
    public OnlineSpeechSettingsWindow ShowSpeechSettings()
    {
        var existing = Application.Current.Windows.OfType<OnlineSpeechSettingsWindow>().FirstOrDefault();
        if (existing is not null) { existing.Show(); existing.Activate(); return existing; }
        var vm = new OnlineSpeechSettingsViewModel(speechSettings, onlineSettings, credentials => new OnlineSpeechSynthesizer(credentials), new WindowsSpeechAudioPlayer());
        vm.Saved += (_, _) => RefreshVoiceModes();
        var window = new OnlineSpeechSettingsWindow(vm); window.Show(); window.Activate(); return window;
    }
    public EditorWindow OpenEditor(EditorViewModel vm)
    {
        var window = new EditorWindow(); vm.RebindDialogs(new EditorDialogs(() => window));
        window.DataContext = vm;
        window.Toolbar.StickerRequested += (_, _) =>
        {
            var picker = new StickerPicker { Owner = window };
            if (picker.ShowDialog() == true && picker.Png is { } png) vm.AddSticker(png, picker.ImageWidth, picker.ImageHeight);
        };
        window.Toolbar.PinRequested += (_, _) => pins.Add(vm.Compose());
        window.Toolbar.OcrRequested += (_, _) => ShowText(vm, false);
        window.Toolbar.TranslateRequested += (_, _) => ShowText(vm, true);
        window.Toolbar.ShareRequested += (_, _) => ShowShare(vm);
        window.Toolbar.WindowRequested += (_, _) => window.Activate();
        window.Toolbar.ScrollRequested += _ => MessageBox.Show(window, "请使用截图快捷键或主窗口框选屏幕内容，再选择网页或桌面手动长截图。", "长截图");
        window.Toolbar.SelectionRequested += (_, _) => MessageBox.Show(window, "请使用截图快捷键或主窗口重新选择屏幕区域。", "调整选区");
        window.Toolbar.CancelRequested += (_, _) => window.CancelScreenshot();
        window.Toolbar.CompleteRequested += async (_, _) =>
        {
            try { if (await vm.CopyAndMarkAsync()) window.Close(); }
            catch (Exception e) { MessageBox.Show(window, e.Message, "复制失败"); }
        };
        window.Show(); window.Activate(); return window;
    }
    public WindowsShareWindow ShowShare(EditorViewModel vm)
    {
        var window = new WindowsShareWindow(vm.Compose(), export, diagnostics, createSession: shareFactory);
        window.Show(); window.Activate(); return window;
    }
    public async Task HandleAsync(SelectionResult result)
    {
        switch (result.Action)
        {
            case SelectionAction.Ocr: ShowText(result.Editor, false); break;
            case SelectionAction.Translate: ShowText(result.Editor, true); break;
            case SelectionAction.Pin: pins.Add(result.Editor.Compose()); break;
            case SelectionAction.Editor: OpenEditor(result.Editor); break;
            case SelectionAction.Share: ShowShare(result.Editor); break;
            case SelectionAction.Scroll:
                if (result.Editor.IsDirty && MessageBox.Show("长截图使用屏幕原画面，不包含当前标注。继续？", "长截图", MessageBoxButton.OKCancel) != MessageBoxResult.OK)
                { OpenEditor(result.Editor); return; }
                await CaptureBrowserAsync(result);
                break;
        }
    }
    public Action<string?>? CaptureStatus { get; set; }
    private CancellationTokenSource? browserCancellation;
    public void CancelBrowserCapture() { if (browserCancellation is { } current) _ = current.CancelAsync(); }
    private async Task CaptureBrowserAsync(SelectionResult result)
    {
        using var cancellation = new CancellationTokenSource(); browserCancellation = cancellation;
        bool desktop = result.BrowserMode == BrowserCaptureMode.DesktopManual;
        using var operation = diagnostics?.Begin(desktop ? "desktop-manual-capture" : "browser-capture", new { Mode = result.BrowserMode.ToString(), result.Region.Width, result.Region.Height });
        System.Windows.Media.Imaging.BitmapSource? image = null;
        Exception? failure = null;
        ManualCaptureProgress? lastProgress = null;
        using var manual = result.BrowserMode is BrowserCaptureMode.ManualScroll or BrowserCaptureMode.DesktopManual ? new ManualScrollCaptureController(() => _ = cancellation.CancelAsync(), desktop) : null;
        try
        {
            // GDI can photograph floating windows even with ShowActivated=false.
            // Hide existing windows; manual controls are created later, outside the captured region.
            using var hidden = new CaptureWindowScope(Application.Current.Windows.Cast<Window>());
            operation?.Stage("own-windows-hidden");
            CaptureStatus?.Invoke(desktop ? "正在准备桌面长截图 · Esc 取消" : "正在采集网页 · Esc 取消");
            await Task.Delay(160, cancellation.Token);
            var progress = new Progress<string>(message => { if (browserCancellation == cancellation && !cancellation.IsCancellationRequested) CaptureStatus?.Invoke(message); });
            var uiFeedback = manual is null ? null : new Progress<ManualCaptureProgress>(value =>
            {
                if (browserCancellation == cancellation && !cancellation.IsCancellationRequested)
                { manual.ViewModel.Update(value); manual.RefreshSelection(); CaptureStatus?.Invoke(value.Status); }
            });
            var feedback = uiFeedback is null ? null : new CaptureProgress(value => { lastProgress = value; ((IProgress<ManualCaptureProgress>)uiFeedback).Report(value); });
            Func<PixelRect, IntPtr, Task>? ready = manual is null ? null : (region, target) => Application.Current.Dispatcher.InvokeAsync(() => { cancellation.Token.ThrowIfCancellationRequested(); manual.Bind(region, target); }).Task;
            Func<bool, Task>? visibility = manual is null ? null : visible => Application.Current.Dispatcher.InvokeAsync(() => manual.SetPreviewVisible(visible)).Task;
            image = desktop
                ? await new DesktopManualScrollCapture(capture).CaptureAsync(result.Region, manual!.DesktopControl!, feedback!, cancellation.Token, operation, ready, visibility)
                : await new BrowserScrollCapture(capture).CaptureAsync(result.Region, progress, cancellation.Token, result.BrowserMode, operation, manual?.Control, feedback, ready, visibility);
            cancellation.Token.ThrowIfCancellationRequested();
            manual?.Dispose();
            operation?.Complete(new { image.PixelWidth, image.PixelHeight });
        }
        catch (OperationCanceledException) { image = null; operation?.Cancel("requested"); }
        catch (Exception e) { failure = e; operation?.Fail(e); }
        finally { manual?.Dispose(); browserCancellation = null; CaptureStatus?.Invoke(null); }
        if (failure is not null)
        {
            if (captureFailure is not null) captureFailure(failure);
            else MessageBox.Show(failure.Message, "长截图未完成", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        if (image is not null)
        {
            var editor = new EditorViewModel(image, renderer, export, new EditorDialogs(() => null));
            if (desktop && lastProgress?.Partial == true) editor.ShowCaptureNotice(lastProgress.Status);
            OpenEditor(editor);
        }
        else OpenEditor(result.Editor);
    }
    private sealed class CaptureProgress(Action<ManualCaptureProgress> report) : IProgress<ManualCaptureProgress>
    { public void Report(ManualCaptureProgress value) => report(value); }
}
