using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Snip.App.ViewModels;
using Snip.Core;
using Snip.Translation;

namespace Snip.Windows.Checks;

internal static class ResponsivenessChecks
{
    public static void Ocr(BitmapSource image)
    {
        var uiThread = Environment.CurrentManagedThreadId;
        var ocr = new SlowOcr();
        string directory = Path.Combine(Path.GetTempPath(), "Snip.Ocr.Diagnostics-" + Guid.NewGuid());
        using var diagnostics = new DiagnosticJournal(directory);
        using var vm = new TextResultViewModel(image, ocr, new ModelStore("unused-models"), new SlowTranslator(), diagnostics) { Source = "已有结果" };
        Require(ocr.LanguageThread == 0); // Constructor must not invoke a native language provider.
        var oldContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            var task = vm.InitializeAsync(); bool ticked = false;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            timer.Tick += (_, _) => { ticked = true; vm.CancelCommand.Execute(null); timer.Stop(); };
            timer.Start(); Pump(task, TimeSpan.FromSeconds(3)); timer.Stop();
            Require(ticked && !vm.Busy && vm.Source == "已有结果" && vm.Status.Contains("取消"));
            Require(ocr.LanguageThread != 0 && ocr.LanguageThread != uiThread);
            diagnostics.FlushAsync().GetAwaiter().GetResult();
            string log = File.ReadAllText(Path.Combine(directory, "events.jsonl"));
            Require(log.Contains("ocr-languages-loading") && log.Contains("cancelled") && !log.Contains("已有结果"));
        }
        finally { SynchronizationContext.SetSynchronizationContext(oldContext); diagnostics.Dispose(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    public static void Translation(BitmapSource image)
    {
        var translator = new SlowTranslator(); var uiThread = Environment.CurrentManagedThreadId;
        using var vm = new TextResultViewModel(image, new SlowOcr(), new ModelStore("unused-models"), translator) { Source = "Hello" };
        var oldContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            var task = vm.TranslateAsync(); bool ticked = false;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            timer.Tick += (_, _) => ticked = true; timer.Start();
            Pump(task, TimeSpan.FromSeconds(3)); timer.Stop();
            Require(ticked && translator.Thread != uiThread && vm.Translated == "你好" && !vm.Busy);
        }
        finally { SynchronizationContext.SetSynchronizationContext(oldContext); }
    }
    private static void Pump(Task task, TimeSpan timeout)
    {
        var frame = new DispatcherFrame(); var watch = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (task.IsCompleted || watch.Elapsed > timeout) frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
        Require(task.IsCompleted); task.GetAwaiter().GetResult();
    }
    private static void Require(bool value) { if (!value) throw new InvalidOperationException("Responsiveness assertion failed"); }
    private sealed class SlowOcr : IOcrService
    {
        public int LanguageThread;
        public IReadOnlyList<string> Languages { get { LanguageThread = Environment.CurrentManagedThreadId; Thread.Sleep(400); return ["en-US"]; } }
        public Task<OcrResult> RecognizeAsync(ImagePayload image, CancellationToken token) => RecognizeAsync(image, null, token);
        public Task<OcrResult> RecognizeAsync(ImagePayload image, string? language, CancellationToken token)
        { Thread.Sleep(400); return Task.FromResult(new OcrResult([new("late result", new(0, 0, 10, 10), null)])); }
    }
    private sealed class SlowTranslator : ITranslationService
    {
        public int Thread;
        public Task<string> TranslateAsync(string text, string direction, CancellationToken token)
        { Thread = Environment.CurrentManagedThreadId; System.Threading.Thread.Sleep(350); token.ThrowIfCancellationRequested(); return Task.FromResult("你好"); }
    }
}
