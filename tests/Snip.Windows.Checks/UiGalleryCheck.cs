using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Snip.App.Services;
using Snip.App.ViewModels;
using Snip.App.Views;
using Snip.Core;
using Snip.Infrastructure;
using Snip.Translation;

namespace Snip.Windows.Checks;

/// <summary>Renders the real WPF windows using public fixtures; never touches a user's screenshot or selects a live sharing target.</summary>
internal static class UiGalleryCheck
{
    public static int Run()
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        Ui.EnsureTheme();
        string stage = "initialization";
        using var watchdog = new System.Threading.Timer(_ => { Console.WriteLine("::error title=UI gallery deadline::" + Volatile.Read(ref stage)); Environment.Exit(1); }, null, TimeSpan.FromSeconds(60), Timeout.InfiniteTimeSpan);
        Exception? dispatcherError = null;
        DispatcherUnhandledExceptionEventHandler handler = (_, e) => { dispatcherError = e.Exception; e.Handled = true; };
        app.DispatcherUnhandledException += handler;
        string output = Path.GetFullPath("artifacts/validation/ui-previews"); Directory.CreateDirectory(output);
        var image = Sample(); var export = new FakeExport();
        var renderer = new AnnotationRenderer();
        try
        {
            void Capture(Window window, string name, Action? ready = null)
            {
                stage = name; Console.WriteLine("::notice title=UI gallery::" + name);
                window.Show(); window.UpdateLayout();
                ready?.Invoke();
                var paint = Task.Delay(80); TextWindowChecks.PumpUntil(() => paint.IsCompleted, TimeSpan.FromSeconds(3));
                if (dispatcherError is not null) throw dispatcherError;
                Require(window.IsLoaded && window.IsVisible && window.ActualWidth > 0, "Window did not render: " + name);
                var content = (FrameworkElement)window.Content;
                if (window is SelectionWindow) CheckSelectionLayout(content);
                foreach (var button in TextWindowChecks.Descendants<Button>(content).Where(b => b.IsVisible && b.ActualWidth > 0))
                {
                    var bounds = button.TransformToAncestor(content).TransformBounds(new Rect(0, 0, button.ActualWidth, button.ActualHeight));
                    Require(bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= content.ActualWidth + 1 && bounds.Bottom <= content.ActualHeight + 1, "Button clipped outside the window in " + name + ": " + button.Content + "; bounds=" + bounds + "; content=" + content.ActualWidth + "x" + content.ActualHeight);
                }
                foreach (var toolbar in TextWindowChecks.Descendants<EditorToolbar>(content))
                    Require(TextWindowChecks.Descendants<RadioButton>(toolbar).Count()==6 && !TextWindowChecks.Descendants<RadioButton>(toolbar).Any(b=>Equals(b.ToolTip,"选择 / 移动 / 缩放标注")), "Selection button remains in an editor or overlay toolbar.");
                if (window is TextResultWindow)
                    foreach (var field in TextWindowChecks.Descendants<TextBox>(content).Where(b => b.IsVisible && b.Name is "RecognizedText" or "TranslatedText"))
                        Require(field.ActualHeight >= 70, "Text result is too short to read in " + name + ": " + field.ActualHeight + " DIP");
                var drawing = new DrawingVisual();
                using (var dc = drawing.RenderOpen())
                {
                    dc.DrawRectangle(window.Background, null, new Rect(0, 0, content.ActualWidth + 48, content.ActualHeight + 48));
                    var offset = VisualTreeHelper.GetOffset(content);
                    var brush = new VisualBrush(content) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(offset.X, offset.Y, content.ActualWidth, content.ActualHeight), Stretch = Stretch.Fill };
                    dc.DrawRectangle(brush, null, new Rect(24, 24, content.ActualWidth, content.ActualHeight));
                }
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth + 48), (int)Math.Ceiling(content.ActualHeight + 48), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(drawing);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var file = File.Create(Path.Combine(output, name + ".png"))) encoder.Save(file);
                if (window is MainWindow main) main.AllowClose = true;
                window.Close();
                Console.WriteLine("PASS real themed WPF window: " + name);
            }
            var main = new MainWindow { DataContext = new MainViewModel(() => Task.CompletedTask, _ => { }, () => Task.CompletedTask) };
            Capture(main, "01-main");
            var editor = new EditorWindow { DataContext = new EditorViewModel(image, renderer, export, new FakeDialogs()) };
            Capture(editor, "02-editor", () =>
            {
                var button = TextWindowChecks.Descendants<RadioButton>(editor).Single(b => Equals(b.ToolTip, "椭圆"));
                button.IsChecked = true;
                Require(((EditorViewModel)editor.DataContext).Tool == AnnotationTool.Ellipse, "Themed tool radio no longer selects the tool.");
                var choice = TextWindowChecks.Descendants<ComboBox>(editor).First(); choice.IsDropDownOpen = true; choice.UpdateLayout(); choice.SelectedIndex = 3; choice.IsDropDownOpen = false;
                Require(((EditorViewModel)editor.DataContext).StrokeWidth == 8, "Themed width dropdown no longer updates the editor.");
                var color = TextWindowChecks.Descendants<Button>(editor).Single(b => Equals(b.ToolTip, "#FF9500"));
                color.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(((EditorViewModel)editor.DataContext).Color == "#FF9500", "Color swatches no longer update the editor.");
                var toolbar = TextWindowChecks.Descendants<EditorToolbar>(editor).Single();
                var browser = TextWindowChecks.Descendants<Button>(toolbar).Single(b => Equals(b.ToolTip, "长截图（网页 / 桌面软件）"));
                var entries = browser.ContextMenu.Items.OfType<MenuItem>().ToArray();
                Require(entries.Length == 4 && browser.ContextMenu.Items.OfType<Separator>().Count() == 1, "Long capture menu must separate three webpage modes and desktop manual capture.");
                var requested = new List<BrowserCaptureMode>(); toolbar.ScrollRequested += requested.Add;
                foreach (var entry in entries) entry.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Require(requested.SequenceEqual(new[] { BrowserCaptureMode.EntirePage, BrowserCaptureMode.FromCurrentPage, BrowserCaptureMode.ManualScroll, BrowserCaptureMode.DesktopManual }), "Long capture menu dispatches an incorrect capture mode.");
            });
            TextResultWindow Result(bool translation) => new(new TextResultViewModel(image, new GalleryOcr(), new ModelStore("unused-models"), new GalleryTranslation()) { AutoTranslate = translation });
            var result = Result(false);
            Capture(result, "03-ocr", () => TextWindowChecks.PumpUntil(() => !((TextResultViewModel)result.DataContext).Busy && ((TextResultViewModel)result.DataContext).Source.Length > 0, TimeSpan.FromSeconds(5)));
            result = Result(true);
            Capture(result, "04-translation", () => TextWindowChecks.PumpUntil(() => !((TextResultViewModel)result.DataContext).Busy && ((TextResultViewModel)result.DataContext).Translated.Length > 0, TimeSpan.FromSeconds(5)));
            result = Result(true); result.Width = result.MinWidth; result.Height = result.MinHeight;
            Capture(result, "05-translation-compact", () => TextWindowChecks.PumpUntil(() => !((TextResultViewModel)result.DataContext).Busy && ((TextResultViewModel)result.DataContext).Translated.Length > 0, TimeSpan.FromSeconds(5)));
            Capture(new TextDialog("为画面添加一点说明。\n截图、标注，然后分享。", "#079B70", 24), "06-text-dialog");
            Capture(new StickerPicker(), "07-stickers");
            Capture(new OnlineTranslationSettingsWindow(new OnlineTranslationSettingsViewModel(new OnlineTranslationSettingsStore(Path.Combine(output,"unused-online-settings.json")))), "09-online-settings");
            using var hotkey = new HotkeyService();
            Capture(new HotkeySettingsWindow(new HotkeySettingsViewModel(hotkey, new HotkeySettingsStore(Path.Combine(output,"unused-hotkey.json")), HotkeyGesture.Default)), "14-hotkey-settings");
            Capture(new WindowsShareWindow(image, export, autoOpen: false), "08-windows-share");
            using var galleryPins=new Snip.App.Services.PinManager(export,()=>{});
            Capture(new PinManagerWindow(galleryPins.ViewModel), "10-pins-empty");
            galleryPins.Add(image);
            var pin=app.Windows.OfType<PinWindow>().Single();
            Capture(new PinManagerWindow(galleryPins.ViewModel), "11-pins-manager");
            Capture(pin, "12-pin");
            Capture(new PinManagerWindow(galleryPins.ViewModel),"24-closed-pin-history");
            using var pinHotkeys=new PinHotkeyService();
            Capture(new PinHotkeySettingsWindow(new PinHotkeySettingsViewModel(pinHotkeys,new PinHotkeySettingsStore(Path.Combine(output,"unused-pin-hotkeys.json")),new())),"25-pin-hotkeys");
            var capture = new FakeCapture(image);
            using var selection = new SelectionSession(new(image, new(0, 0, 1280, 800), capture.GetDisplays()), capture, renderer, export, []);
            selection.Show(); selection.ViewModel.SetSelection(new(90, 110, 1050, 460)); selection.BeginEditing();
            Capture(app.Windows.OfType<SelectionWindow>().Single(), "13-selection-toolbar");
            var selectionLayouts = new List<object>();
            foreach (var (name, width, height) in new[] {
                ("37-selection-fullscreen", 1280.0, 800.0),
                ("38-selection-scaled", 960.0, 600.0),
                ("39-selection-compact", 640.0, 500.0) })
            {
                using var edgeSelection = new SelectionSession(new(image, new(0, 0, 1280, 800), capture.GetDisplays()), capture, renderer, export, []);
                edgeSelection.Show();
                var window = app.Windows.OfType<SelectionWindow>().Single();
                window.Width = width; window.Height = height; window.UpdateLayout();
                foreach (var (label, region) in new[] {
                    ("full", new PixelRect(0, 0, 1280, 800)),
                    ("top-edge", new PixelRect(20, 10, 1200, 780)),
                    ("bottom-edge", new PixelRect(25, 620, 1150, 180)),
                    ("ordinary", new PixelRect(90, 110, 600, 360)) })
                {
                    stage = name + "/" + label;
                    edgeSelection.ViewModel.SetSelection(region); edgeSelection.BeginEditing(); window.UpdateLayout();
                    CheckSelectionLayout((FrameworkElement)window.Content);
                    var settled = Task.Delay(40); TextWindowChecks.PumpUntil(() => settled.IsCompleted, TimeSpan.FromSeconds(3));
                    CheckSelectionLayout((FrameworkElement)window.Content);
                    selectionLayouts.Add(new { Case = name + "/" + label, Width = window.ActualWidth, Height = window.ActualHeight, HintAndToolbarVisible = true, NoOverlap = true, InsideWindow = true });
                }
                edgeSelection.AdjustSelection(); window.UpdateLayout();
                var hint = TextWindowChecks.Descendants<Border>(window).Single(b => b.Name == "SelectionHint");
                Require(Canvas.GetTop(hint) == 16 && !TextWindowChecks.Descendants<EditorToolbar>(window).Single().IsVisible, "Reselecting must restore the hint and hide the toolbar.");
                edgeSelection.ViewModel.SetSelection(new(0, 0, 1280, 800)); edgeSelection.BeginEditing(); window.UpdateLayout();
                var rendered = edgeSelection.Editor!.Compose();
                Require(rendered.PixelWidth == image.PixelWidth && rendered.PixelHeight == image.PixelHeight, "Overlay layout changed the screenshot size.");
                var sourcePixels = new byte[image.PixelWidth * image.PixelHeight * 4];
                var renderedPixels = new byte[sourcePixels.Length];
                image.CopyPixels(sourcePixels, image.PixelWidth * 4, 0); rendered.CopyPixels(renderedPixels, image.PixelWidth * 4, 0);
                Require(sourcePixels.SequenceEqual(renderedPixels), "Hint or toolbar leaked into screenshot pixels.");
                Capture(window, name);
            }
            File.WriteAllText(Path.Combine(output, "..", "selection-layout.json"), JsonSerializer.Serialize(new { Cases = selectionLayouts, ReselectResetsHint = true, ExportPixelsUnchanged = true }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("PASS selection layout: 12 full/top/bottom/ordinary cases at 1280x800, 960x600 and 640x500 DIPs; reselect resets; original export pixels unchanged.");
            using var manualControl = new DragCaptureControl();
            manualControl.Configure(900, 600); manualControl.Begin(new(50, 50)); manualControl.Move(new(450, 580));
            var manualVm = new ManualScrollCaptureViewModel(manualControl, () => { });
            manualVm.Update(new(4, 950, "按住拖选 · 下拉过网页 60% 处滚动 · Esc 取消", true));
            manualVm.RefreshSelection();
            var manualWindow = new ManualScrollCaptureWindow(manualVm);
            Capture(manualWindow, "15-manual-scroll", () =>
            {
                var done = TextWindowChecks.Descendants<Button>(manualWindow).Single(b => b.Name == "CompleteManualButton");
                Require(done.IsEnabled, "Manual finish button is not usable after a valid frame.");
                done.Command.Execute(done.CommandParameter);
                Require(manualControl.FinishRequested, "Manual finish button did not end collection.");
            });
            Capture(new ApplicationSettingsWindow(new ApplicationSettingsViewModel(new GalleryStartup(),new TranslationPreferencesStore(Path.Combine(output,"unused-preferences.json")),new(TranslationMode.Basic))), "16-application-settings");
            var basic=new TextResultWindow(new TextResultViewModel(image,new GalleryOcr(),new ModelStore("unused-models"),new GalleryTranslation(),initialPreferences:new(TranslationMode.Basic)){AutoTranslate=true});
            Capture(basic,"17-basic-dictionary",()=>TextWindowChecks.PumpUntil(()=>!((TextResultViewModel)basic.DataContext).Busy && ((TextResultViewModel)basic.DataContext).Translated.Length>0,TimeSpan.FromSeconds(5)));
            foreach(var (name,chinese,compact) in new[]{("18-online-ipa-pinyin",false,false),("19-online-english-vocabulary",true,false),("20-online-annotations-compact",false,true)})
            {
                var annotated=new TextResultWindow(new TextResultViewModel(image,new GalleryOcr(chinese),new ModelStore("unused-models"),new GalleryTranslation(),onlineTranslator:new GalleryAnnotated(),initialPreferences:new(TranslationMode.Online)){AutoTranslate=true});
                if(compact){annotated.Width=annotated.MinWidth;annotated.Height=annotated.MinHeight;}
                else {annotated.Height=1000;}
                Capture(annotated,name,()=>
                {
                    var vm=(TextResultViewModel)annotated.DataContext;
                    TextWindowChecks.PumpUntil(()=>!vm.Busy && vm.HasLanguageNotes,TimeSpan.FromSeconds(5));
                    Require(vm.Direction=="auto" && vm.LanguageNotes.Contains("音标") && vm.LanguageNotes.Contains("名词(n.)") && vm.LanguageNotes.Contains("中文译文拼音")==!chinese,"Online annotation preview has the wrong language notes.");
                    if(!chinese)((TabItem)annotated.FindName("LanguageNotesTab")).IsSelected=true;
                });
            }
            var longVm=new TextResultViewModel(image,new GalleryOcr(true),new ModelStore("unused-models"),new GalleryTranslation(),onlineTranslator:new GalleryLong(),initialPreferences:new(TranslationMode.Online)){AutoTranslate=true};
            Capture(new TextResultWindow(longVm),"21-long-translation",()=>TextWindowChecks.PumpUntil(()=>!longVm.Busy && longVm.Translated.Length>0,TimeSpan.FromSeconds(5)));
            using var readingVm=new TextResultViewModel(image,new GalleryOcr(true),new ModelStore("unused-models"),new GalleryTranslation(),onlineTranslator:new GalleryLong(),initialPreferences:new(TranslationMode.Online)){AutoTranslate=true};
            var initialized=readingVm.InitializeAsync();TextWindowChecks.PumpUntil(()=>initialized.IsCompleted,TimeSpan.FromSeconds(5));initialized.GetAwaiter().GetResult();
            Capture(new TranslationReadingWindow(readingVm),"22-reading-view");
            Capture(new SourceReadingWindow(readingVm),"32-source-reading");
            var sourceCompact = new SourceReadingWindow(readingVm);
            sourceCompact.Width=sourceCompact.MinWidth;sourceCompact.Height=sourceCompact.MinHeight;
            Capture(sourceCompact,"33-source-reading-compact");
            var expandedVm=new TextResultViewModel(image,new GalleryOcr(true),new ModelStore("unused-models"),new GalleryTranslation(),onlineTranslator:new GalleryLong(),initialPreferences:new(TranslationMode.Online)){AutoTranslate=true};
            var expanded=new TextResultWindow(expandedVm);expanded.Width=expanded.MinWidth;expanded.Height=expanded.MinHeight;
            Capture(expanded,"23-compact-expanded-source",()=>{TextWindowChecks.PumpUntil(()=>!expandedVm.Busy && expandedVm.Translated.Length>0,TimeSpan.FromSeconds(5));expandedVm.SourceExpanded=true;});
            var voiceTranslation = new OnlineTranslationSettingsStore(Path.Combine(output, "unused-speech-translation.json"));
            var voiceStore = new OnlineSpeechSettingsStore(voiceTranslation, Path.Combine(output, "unused-speech.json"));
            OnlineSpeechSettingsWindow VoiceWindow() => new(new OnlineSpeechSettingsViewModel(voiceStore, voiceTranslation, credentials => new OnlineSpeechSynthesizer(credentials), new WindowsSpeechAudioPlayer()));
            Capture(VoiceWindow(), "34-ai-speech-settings");
            var narrowVoice = VoiceWindow(); narrowVoice.Width = narrowVoice.MinWidth;
            Capture(narrowVoice, "35-ai-speech-settings-narrow");
            Console.WriteLine("PASS 38 themed UI previews including full-screen/edge/scaled selection layouts and reading views; three browser menu actions; no share target invoked or user screenshot used."); return 0;
        }
        catch (Exception error)
        {
            Console.WriteLine("::error title=UI gallery::" + stage + ": " + error.ToString().Replace("%", "%25").Replace("\r", "%0D").Replace("\n", "%0A")); return 1;
        }
        finally
        {
            foreach (Window window in app.Windows.Cast<Window>().ToArray()) { if (window is MainWindow main) main.AllowClose = true; window.Close(); }
            app.DispatcherUnhandledException -= handler; SynchronizationContext.SetSynchronizationContext(previous);
        }
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void CheckSelectionLayout(FrameworkElement content)
    {
        var toolbar = TextWindowChecks.Descendants<EditorToolbar>(content).Single();
        var hint = TextWindowChecks.Descendants<Border>(content).Single(b => b.Name == "SelectionHint");
        Require(toolbar.IsVisible && hint.IsVisible && hint.ActualWidth > 0 && hint.ActualHeight > 0, "Selection controls must remain visible.");
        Rect Bounds(FrameworkElement element) => element.TransformToAncestor(content).TransformBounds(new Rect(element.RenderSize));
        var hintBounds = Bounds(hint); var toolbarBounds = Bounds(toolbar);
        Require(!hintBounds.IntersectsWith(toolbarBounds), "Selection hint overlaps the toolbar: " + hintBounds + " / " + toolbarBounds);
        foreach (var bounds in new[] { hintBounds, toolbarBounds })
            Require(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= content.ActualWidth + 1 && bounds.Bottom <= content.ActualHeight + 1, "Selection controls extend outside the window: " + bounds);
    }
    private static BitmapSource Sample()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(243, 248, 247)), null, new Rect(0, 0, 1280, 800));
            dc.DrawRoundedRectangle(Brushes.White, null, new Rect(70, 70, 1140, 660), 22, 22);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(7, 155, 112)), null, new Rect(110, 110, 72, 72), 18, 18);
            void Text(string text, double size, Brush brush, double x, double y) => dc.DrawText(new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, 1), new Point(x, y));
            Text("A little clarity, every day.", 40, Brushes.DarkSlateGray, 110, 225);
            Text("Capture an idea. Make it yours.", 24, Brushes.SlateGray, 110, 295);
            Text("Keep the details that matter.", 24, Brushes.SlateGray, 110, 345);
            Text("SCREENSHOT  /  ANNOTATE  /  SHARE", 17, Brushes.SeaGreen, 110, 625);
            for (int i = 0; i < 3; i++) dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb((byte)(225 + i * 5), (byte)(242 - i * 5), (byte)(235 + i * 4))), null, new Rect(760 + i * 90, 170 + i * 105, 170, 170), 22, 22);
        }
        var bitmap = new RenderTargetBitmap(1280, 800, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Freeze(); return bitmap;
    }
    private sealed class GalleryStartup : IAutoStartService {public bool IsEnabled=>false;public void SetEnabled(bool enabled){}}
    private sealed class GalleryOcr(bool chinese=false) : IOcrService
    {
        public IReadOnlyList<string> Languages => ["en-US", "zh-CN"];
        public Task<OcrResult> RecognizeAsync(ImagePayload image, CancellationToken token) => RecognizeAsync(image, null, token);
        public Task<OcrResult> RecognizeAsync(ImagePayload image, string? language, CancellationToken token) => Task.FromResult(new OcrResult([new(chinese ? "每天，让思路更清晰一点。捕捉一个灵感，留下重要的细节。" : "A little clarity, every day.\r\n\r\nCapture an idea. Make it yours.\r\nKeep the details that matter.", new(0, 0, 300, 120), null)]));
    }
    private sealed class GalleryTranslation : ITranslationService
    { public Task<string> TranslateAsync(string text, string direction, CancellationToken token) => Task.FromResult("每天，让思路更清晰一点。\r\n\r\n捕捉一个灵感，让它成为你的作品。\r\n留下那些重要的细节。"); }
    private sealed class GalleryAnnotated : IAnnotatedTranslationService
    {
        public Task<string> TranslateAsync(string text,string direction,CancellationToken token)=>throw new InvalidOperationException("Gallery must use annotated translation.");
        public Task<AnnotatedTranslation> TranslateWithDetailsAsync(string text,string direction,IProgress<TranslationProgress>? progress,CancellationToken token)
            =>Task.FromResult(new AnnotatedTranslation(direction=="en-zh" ? "每天，让思路更清晰一点。\n捕捉一个灵感，留下重要的细节。" : "A little clarity, every day.\nCapture an idea. Keep the details that matter.",
                direction=="en-zh" ? "Měi tiān, ràng sī lù gèng qīng xī yì diǎn.\nBǔ zhuō yí gè líng gǎn, liú xià zhòng yào de xì jié." : "",
                [new("clarity","/ˈklærəti/","名词(n.)","清晰；明确"),new("capture","/ˈkæptʃər/","动词(v.)","捕捉；记录"),new("detail","/ˈdiːteɪl/","名词(n.)","细节")]));
    }
    private sealed class GalleryLong : IAnnotatedTranslationService
    {
        public Task<string> TranslateAsync(string text,string direction,CancellationToken token)=>Task.FromResult(TextReadingChecks.LongBody);
        public Task<AnnotatedTranslation> TranslateWithDetailsAsync(string text,string direction,IProgress<TranslationProgress>? progress,CancellationToken token)
            =>Task.FromResult(new AnnotatedTranslation(TextReadingChecks.LongBody,"",[new("capture","/ˈkæptʃər/","动词(v.)","捕捉；记录")]));
    }
    private sealed class FakeExport : IImageExportService
    { public void SavePng(BitmapSource image, string path) { } public Task CopyAsync(BitmapSource image) => Task.CompletedTask; }
    private sealed class FakeDialogs : IEditorDialogs
    { public string? ChoosePngPath() => null; public TextInput? EditText(string text, string color, double fontSize) => null; public CloseChoice ConfirmClose() => CloseChoice.Discard; public void ShowError(string message) => throw new InvalidOperationException(message); }
    private sealed class FakeCapture(BitmapSource image) : IDesktopCaptureService
    {
        private readonly DisplayInfo display = new("Gallery", new(0, 0, 1280, 800), true);
        public IReadOnlyList<DisplayInfo> GetDisplays() => [display]; public DesktopFrame Capture() => new(image, display.Bounds, GetDisplays()); public PixelPoint CursorPosition => new(140, 150);
        public void PlaceOverlay(Window window, PixelRect bounds) { window.Left = window.Top = 0; }
    }
}
