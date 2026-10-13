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
    private readonly OfflineOcrService ocr = new();
    private readonly ModelStore models = new(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SelfSnip", "Models"));
    private readonly OfflineTranslator translator;
    public FeatureHub(AnnotationRenderer renderer, IImageExportService export, IDesktopCaptureService capture, PinManager pins)
    { this.renderer = renderer; this.export = export; this.capture = capture; this.pins = pins; translator = new(models); }
    public void ShowText(EditorViewModel vm, Window owner, bool translate)
    {
        var text = new TextResultViewModel(vm.Compose(), ocr, models, translator) { AutoTranslate = translate };
        var window = new TextResultWindow(text) { Owner = owner }; window.ShowDialog();
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
        window.Toolbar.OcrRequested += (_, _) => ShowText(vm, window, false);
        window.Toolbar.TranslateRequested += (_, _) => ShowText(vm, window, true);
        window.Toolbar.WeChatRequested += (_, _) => ShowWeChat(vm, window);
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
    public void ShowWeChat(EditorViewModel vm, Window owner) => new WeChatShareWindow(vm.Compose(), export) { Owner = owner }.ShowDialog();
    public async Task HandleAsync(SelectionResult result)
    {
        switch (result.Action)
        {
            case SelectionAction.Pin: pins.Add(result.Editor.Compose()); break;
            case SelectionAction.Editor: OpenEditor(result.Editor); break;
            case SelectionAction.WeChat:
                var editor = OpenEditor(result.Editor); ShowWeChat(result.Editor, editor); break;
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
        System.Windows.Media.Imaging.BitmapSource? image = null;
        Exception? failure = null;
        try
        {
            // No floating progress window: GDI can photograph it even with ShowActivated=false.
            // Hide every WPF window we own, including topmost pins, until the last frame is done.
            using var hidden = new CaptureWindowScope(Application.Current.Windows.Cast<Window>());
            CaptureStatus?.Invoke("正在采集网页 · Esc 取消");
            await Task.Delay(160, cancellation.Token);
            var progress = new Progress<string>(message => { if (browserCancellation == cancellation && !cancellation.IsCancellationRequested) CaptureStatus?.Invoke(message); });
            image = await new BrowserScrollCapture(capture).CaptureAsync(result.Region, progress, cancellation.Token, result.BrowserMode);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { failure = e; }
        finally { browserCancellation = null; CaptureStatus?.Invoke(null); }
        if (failure is not null) MessageBox.Show(failure.Message, "长截图未完成", MessageBoxButton.OK, MessageBoxImage.Warning);
        if (image is not null) OpenEditor(new EditorViewModel(image, renderer, export, new EditorDialogs(() => null)));
        else OpenEditor(result.Editor);
    }

}
