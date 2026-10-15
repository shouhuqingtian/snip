using System.Windows.Media.Imaging;
using Snip.App.ViewModels;
using Snip.App.Views;
using Snip.Core;
using Snip.Infrastructure;
using Snip.Translation;

namespace Snip.Windows.Checks;

internal static class TextOptimizationChecks
{
    public static void Run(BitmapSource image)
    {
        string layout = string.Join("\r\n\r\n", Enumerable.Range(0, 12).Select(i =>
            "   申请部门：" + new string(' ', 2048) + "采购 " + i + "   "));
        var translator = new ControlledTranslation();
        var clipboard = new Clipboard();
        using var vm = new TextResultViewModel(image, new LayoutOcr(layout), new ModelStore("unused-models"), translator, clipboard: clipboard) { AutoTranslate = true };
        var window = new TextResultWindow(vm);
        try
        {
            window.Show();
            TextWindowChecks.PumpUntil(() => !vm.Busy && vm.Source.Length > 0, TimeSpan.FromSeconds(8));
            Require(layout.Length > 12000 && vm.Source == layout && vm.HasSourceLayout, "Displayed OCR layout changed.");
            Require(translator.LastInput == TextContent.WithoutLayoutPadding(layout) && vm.Translated == "已完成翻译", "Visual padding consumed translation limit or actual workflow failed.");
            Require(vm.SourceSummary == "识别文字 · " + TextContent.CountCharacters(layout) + " 字", "Summary counts layout spaces as characters.");
            vm.CopySourceCommand.Execute(null);
            TextWindowChecks.PumpUntil(() => vm.CopyStatus.Contains("已复制") && vm.CopySourceCommand.CanExecute(null), TimeSpan.FromSeconds(3));
            Require(clipboard.Text == layout, "Translation normalization changed copied source layout.");
            var openSource = (System.Windows.Controls.Button)window.FindName("OpenSourceReadingButton");
            openSource.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            var reader = window.OwnedWindows.OfType<SourceReadingWindow>().Single(); reader.UpdateLayout();
            var sourceBox = (System.Windows.Controls.TextBox)reader.FindName("ReadingSource");
            Require(sourceBox.Text == layout && sourceBox.TextWrapping == System.Windows.TextWrapping.NoWrap && sourceBox.HorizontalScrollBarVisibility == System.Windows.Controls.ScrollBarVisibility.Auto && sourceBox.FontFamily.Source.Contains("Consolas"), "Source reader changed OCR alignment or omitted horizontal navigation.");
            reader.Close();

            translator.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = vm.TranslateAsync();
            TextWindowChecks.PumpUntil(() => translator.Waiting, TimeSpan.FromSeconds(3));
            // The source is intentionally editable while a remote/native request is running.
            vm.Source = "Updated source";
            translator.Pending.SetResult("过期译文不应覆盖现有结果");
            Await(pending);
            Require(vm.Source == "Updated source" && vm.Translated == "已完成翻译" && vm.Status.Contains("原文已更改"), "An old request replaced translation after the source was edited.");
            translator.Pending = null;
            Await(vm.TranslateAsync());
            Require(translator.LastInput == "Updated source" && vm.Status.Contains("翻译完成"), "Retranslation did not use edited text.");
            Console.WriteLine("PASS actual text workflow: oversized OCR padding omitted only from translation; source/copy retain layout, character count excludes padding, stale request cannot overwrite edited source result; retry works.");
        }
        finally { window.Close(); }
    }
    private static void Await(Task task)
    { TextWindowChecks.PumpUntil(() => task.IsCompleted, TimeSpan.FromSeconds(5)); task.GetAwaiter().GetResult(); }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class LayoutOcr(string layout) : IOcrService
    {
        public IReadOnlyList<string> Languages => ["en-US"];
        public Task<OcrResult> RecognizeAsync(ImagePayload image, CancellationToken token)
            => Task.FromResult(new OcrResult([new("申请部门", new(0, 0, 80, 20), null)], layout));
        public Task<OcrResult> RecognizeAsync(ImagePayload image, string? language, CancellationToken token)
            => RecognizeAsync(image, token);
    }
    private sealed class Clipboard : ITextClipboardService
    {
        public string Text = "";
        public Task CopyAsync(string text, CancellationToken token, IProgress<int>? retries = null) { Text = text; return Task.CompletedTask; }
    }
    private sealed class ControlledTranslation : ITranslationService
    {
        public volatile string? LastInput;
        public volatile bool Waiting;
        public TaskCompletionSource<string>? Pending;
        public Task<string> TranslateAsync(string text, string direction, CancellationToken token)
        {
            LastInput = text;
            if (text.Length > 12000) throw new InvalidOperationException("Translation limit exceeded.");
            if (Pending is { } pending) { Waiting = true; return pending.Task.WaitAsync(token); }
            return Task.FromResult("已完成翻译");
        }
    }
}
