using System.Diagnostics;
using System.IO;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snip.App.ViewModels;
using Snip.App.Views;
using Snip.Core;
using Snip.Infrastructure;
using Snip.Ocr;
using Snip.Translation;

namespace Snip.Windows.Checks;

internal static class OcrModeChecks
{
    public static void Run(string folder)
    {
        var preferences = new OcrPreferencesStore(Path.Combine(folder, "ocr-mode.json"));
        Require(preferences.Read() is null, "Fresh OCR preferences present.");
        foreach (var mode in Enum.GetValues<OcrMode>())
        {
            preferences.Save(mode);
            Require(new OcrPreferencesStore(Path.Combine(folder, "ocr-mode.json")).Read() == mode, "OCR default did not survive restart.");
        }
        File.WriteAllText(Path.Combine(folder, "ocr-mode.json"), "{\"Version\":1,\"Mode\":99}");
        try { preferences.Read(); throw new Exception("Invalid mode accepted."); } catch (InvalidOperationException) { }
        preferences.Save(OcrMode.System);
        var native = new FixtureOcr("系统识别文字");
        var online = new FixtureOcr("Google\n是否需要报关：");
        var enhanced = new EnhancedOcrService(native, new OcrModelStore(Path.Combine(folder, "missing-ocr-models")), () => throw new Exception("Model worker unexpectedly started."));
        SelectableOcrService Router(OcrMode mode) => new(native, enhanced, online, mode);
        var router = Router(OcrMode.System);
        using var vm = new TextResultViewModel(TextWindowChecks.Image(480, 320), router, new ModelStore(Path.Combine(folder, "no-translation-model")), new TranslationTrap(), ocrPreferences: preferences);
        var window = new TextResultWindow(vm); window.Show();
        try
        {
            Wait(() => !vm.Busy && vm.Source.Length > 0);
            Require(native.Calls == 1 && online.Calls == 0 && vm.Source == "系统识别文字", "System mode used online service.");
            var selector = (ComboBox)window.FindName("OcrModeBox");
            Require(selector.IsVisible && selector.Items.Count == 3 && selector.IsEnabled, "Three-way OCR selector missing.");
            selector.SelectedValue = OcrMode.Online;
            window.UpdateLayout();
            Require(vm.RecognitionMode == OcrMode.Online, "Actual selector did not switch the OCR mode.");
            Require(online.Calls == 0, "Changing mode sent screenshot without re-recognizing.");
            Require(vm.OcrModeNotice.Contains("当前截图") && vm.OcrModeNotice.Contains("发送"), "Online screenshot disclosure missing.");
            Require(preferences.Read() == OcrMode.System, "Unsaved selection overwrote default.");
            ((Button)window.FindName("SaveOcrSettingsButton")).Command.Execute(null);
            Require(preferences.Read() == OcrMode.Online && vm.Status.Contains("已保存默认识别方式"), "Actual save-default button failed.");
            var independent = Router(preferences.Read()!.Value);
            Require(independent.Mode == OcrMode.Online, "Saved online default lost.");
            independent.Mode = OcrMode.System;
            Require(router.Mode == OcrMode.Online, "One window changed another window's engine.");
            vm.OcrCommand.Execute(null); Wait(() => !vm.Busy && vm.Source.StartsWith("Google"));
            Require(online.Calls == 1 && native.Calls == 1 && vm.Status.Contains("在线视觉 OCR"), "Online result did not reach visible window.");
            string savedSource = vm.Source;
            online.Fail = true; vm.OcrCommand.Execute(null); Wait(() => !vm.Busy && vm.Status.Contains("失败"));
            Require(window.IsVisible && vm.Source == savedSource && ((TextBox)window.FindName("RecognizedText")).Text == savedSource, "Failure closed result or discarded previous text.");
            selector.SelectedValue = OcrMode.EnhancedOffline;
            vm.OcrCommand.Execute(null); Wait(() => !vm.Busy && vm.Status.Contains("尚未下载"));
            Require(vm.Source == savedSource && native.Calls == 1 && online.Calls == 2, "Missing enhanced model silently used another route.");
            selector.SelectedValue = OcrMode.System; vm.SaveOcrSettingsCommand.Execute(null);
            vm.OcrCommand.Execute(null); Wait(() => !vm.Busy && vm.Source == "系统识别文字");
            Require(online.Calls == 2 && preferences.Read() == OcrMode.System, "Switching back to local kept contacting service.");
            selector.SelectedValue = OcrMode.Online; online.Fail = false; online.Pending = true;
            vm.OcrCommand.Execute(null); Wait(() => online.StartedPending);
            Require(!selector.IsEnabled && !vm.SaveOcrSettingsCommand.CanExecute(null), "OCR mode can change during upload.");
            vm.CancelCommand.Execute(null); Wait(() => !vm.Busy && online.Cancelled);
            Require(window.IsVisible && vm.Source == "系统识别文字", "Cancel discarded previous text.");
            online.Pending = false;
            vm.OcrCommand.Execute(null); Wait(() => !vm.Busy && vm.Source.StartsWith("Google"));
            window.UpdateLayout();
            string preview = Path.GetFullPath("artifacts/validation/ui-previews/27-ocr-modes.png"); Directory.CreateDirectory(Path.GetDirectoryName(preview)!);
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var file = File.Create(preview)) encoder.Save(file);
            online.Pending = true; online.StartedPending = false; online.Cancelled = false;
            vm.OcrCommand.Execute(null); Wait(() => online.StartedPending); window.Close(); Wait(() => online.Cancelled);
            Require(!Directory.Exists(Path.Combine(folder, "missing-ocr-models")), "Mode selection downloaded or loaded models.");
            Console.WriteLine("PASS actual three-way OCR XAML: explicit upload, independent defaults/windows, local no-network, missing models, failure preservation, cancel and close.");
        }
        finally { window.Close(); }
    }
    private static void Wait(Func<bool> condition) => TextWindowChecks.PumpUntil(condition, TimeSpan.FromSeconds(8));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class FixtureOcr(string text) : IOcrService
    {
        public int Calls;
        public bool Fail, Pending, StartedPending, Cancelled;
        public IReadOnlyList<string> Languages => ["zh-Hans-CN", "en-US"];
        public Task<OcrResult> RecognizeAsync(ImagePayload image, CancellationToken token) => RecognizeAsync(image, null, token);
        public async Task<OcrResult> RecognizeAsync(ImagePayload image, string? language, CancellationToken token)
        {
            Calls++;
            if (Fail) throw new InvalidOperationException("PUBLIC service unavailable fixture");
            if (Pending)
            {
                StartedPending = true;
                try { await Task.Delay(Timeout.Infinite, token); } catch (OperationCanceledException) { Cancelled = true; throw; }
            }
            token.ThrowIfCancellationRequested();
            return new([new(text, new(0, 0, image.Width, image.Height), null)]);
        }
    }
    private sealed class TranslationTrap : ITranslationService
    { public Task<string> TranslateAsync(string text, string direction, CancellationToken token) => throw new Exception("OCR unexpectedly translated text."); }
}
