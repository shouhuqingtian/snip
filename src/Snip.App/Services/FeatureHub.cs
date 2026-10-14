using System.Windows;
using System.Windows.Controls;
using Snip.App.ViewModels;
using Snip.App.Views;
using Snip.Core;
using Snip.Infrastructure;
using Snip.Translation;

namespace Snip.App.Services;

/// <summary>Composes optional desktop workflows without making editing depend on OCR or automation.</summary>
public sealed class FeatureHub
{
    private readonly AnnotationRenderer renderer;
    private readonly IImageExportService export;
    private readonly IDesktopCaptureService capture;
    private readonly PinManager pins;
    private readonly IOcrService ocr;
    private readonly ModelStore models;
    private readonly ITranslationService translator;
    private readonly DiagnosticJournal? diagnostics;
    private readonly Func<IntPtr, IWindowsShareSession>? shareFactory;
    public FeatureHub(AnnotationRenderer renderer, IImageExportService export, IDesktopCaptureService capture, PinManager pins, DiagnosticJournal? diagnostics = null,
        IOcrService? ocr = null, ITranslationService? translator = null, ModelStore? models = null, Func<IntPtr, IWindowsShareSession>? shareFactory = null)
    {
        this.renderer = renderer; this.export = export; this.capture = capture; this.pins = pins; this.diagnostics = diagnostics;
        this.ocr = ocr ?? new OfflineOcrService();
        this.models = models ?? new(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SelfSnip", "Models"));
        this.translator = translator ?? new OfflineTranslator(this.models); this.shareFactory = shareFactory;
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
            text = new TextResultViewModel(image, ocr, models, translator, diagnostics) { AutoTranslate = translate };
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
        window.Toolbar.ScrollRequested += _ => MessageBox.Show(window, "请通过 Ctrl+Alt+A 选取 Chrome/Edge 网页正文，再选择网页截图模式。", "网页截图");
        window.Toolbar.SelectionRequested += (_, _) => MessageBox.Show(window, "请通过 Ctrl+Alt+A 重新选择屏幕区域。", "调整选区");
        window.Toolbar.CancelRequested += (_, _) => window.Close();
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
                if (result.BrowserMode == BrowserCaptureMode.SelectedRegion) { OpenEditor(result.Editor); return; }
                if (result.Editor.IsDirty && MessageBox.Show("自动长截图使用网页原画面，不包含当前标注。继续？", "自动长截图", MessageBoxButton.OKCancel) != MessageBoxResult.OK)
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
        using var operation = diagnostics?.Begin("browser-capture", new { Mode = result.BrowserMode.ToString(), result.Region.Width, result.Region.Height });
        System.Windows.Media.Imaging.BitmapSource? image = null;
        Exception? failure = null;
        try
        {
            // No floating progress window: GDI can photograph it even with ShowActivated=false.
            // Hide every WPF window we own, including topmost pins, until the last frame is done.
            using var hidden = new CaptureWindowScope(Application.Current.Windows.Cast<Window>());
            operation?.Stage("own-windows-hidden");
            CaptureStatus?.Invoke("正在采集网页 · Esc 取消");
            await Task.Delay(160, cancellation.Token);
            var progress = new Progress<string>(message => { if (browserCancellation == cancellation && !cancellation.IsCancellationRequested) CaptureStatus?.Invoke(message); });
            image = await new BrowserScrollCapture(capture).CaptureAsync(result.Region, progress, cancellation.Token, result.BrowserMode, operation);
            operation?.Complete(new { image.PixelWidth, image.PixelHeight });
        }
        catch (OperationCanceledException) { operation?.Cancel("requested"); }
        catch (Exception e) { failure = e; operation?.Fail(e); }
        finally { browserCancellation = null; CaptureStatus?.Invoke(null); }
        if (failure is not null) MessageBox.Show(failure.Message, "长截图未完成", MessageBoxButton.OK, MessageBoxImage.Warning);
        if (image is not null) OpenEditor(new EditorViewModel(image, renderer, export, new EditorDialogs(() => null)));
        else OpenEditor(result.Editor);
    }

}
