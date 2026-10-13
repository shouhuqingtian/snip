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
                using (var cancellation = new CancellationTokenSource())
                {
                    var progressWindow = new Window { Title = "正在自动长截图 · Esc取消", Width = 400, Height = 170, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.CenterScreen };
                    var panel = new StackPanel { Margin = new Thickness(16) };
                    var label = new TextBlock { Text = "正在激活浏览器…采集时不要切换窗口，Esc 取消。", TextWrapping = TextWrapping.Wrap }; panel.Children.Add(label);
                    var cancel = new Button { Content = "取消采集" }; cancel.Click += (_, _) => cancellation.Cancel(); panel.Children.Add(cancel); progressWindow.Content = panel;
                    progressWindow.Closing += (_, _) => cancellation.Cancel(); progressWindow.Show();
                    try
                    {
                        var service = new BrowserScrollCapture(capture);
                        var image = await service.CaptureAsync(result.Region, new Progress<string>(message => label.Text = message), cancellation.Token, result.BrowserMode);
                        progressWindow.Close();
                        OpenEditor(new EditorViewModel(image, renderer, export, new EditorDialogs(() => null)));
                    }
                    catch (OperationCanceledException) { OpenEditor(result.Editor); }
                    catch (Exception e) { MessageBox.Show(e.Message, "长截图未完成", MessageBoxButton.OK, MessageBoxImage.Warning); OpenEditor(result.Editor); }
                    finally { progressWindow.Close(); }
                }
                break;
        }
    }
}
