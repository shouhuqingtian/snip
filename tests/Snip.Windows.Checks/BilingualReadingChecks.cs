using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Snip.App.ViewModels;
using Snip.App.Views;
using Snip.Core;
using Snip.Infrastructure;
using Snip.Translation;

namespace Snip.Windows.Checks;

internal static class BilingualReadingChecks
{
    public static void Run(string folder, BitmapSource image)
    {
        string path = Path.Combine(folder, "reading-layout.json");
        var layout = new TextReadingPreferencesStore(path);
        var speech = new Speech(); var translator = new Translation();
        var defaults = new TranslationPreferencesStore(Path.Combine(folder, "reading-translation-defaults.json"));
        defaults.Save(new(TranslationMode.Basic));
        string defaultJson = File.ReadAllText(Path.Combine(folder, "reading-translation-defaults.json"));
        TextResultViewModel Create(TranslationMode mode, ITextReadingPreferencesStore? saved = null) => new(image, new TextWindowChecks.FakeOcr(),
            new ModelStore(Path.Combine(folder, "bilingual-no-models")), translator, onlineTranslator: translator, basicTranslator: translator,
            speech: speech, preferences: defaults, initialPreferences: new(mode), readingPreferences: saved ?? new TextReadingPreferencesStore(path));
        using (var vm = Create(TranslationMode.Online))
        {
            var window = new TextResultWindow(vm); window.Show();
            Wait(() => !vm.Busy && vm.Source.Length > 0);
            vm.Source = "中文原文，不应上传到语音服务。";
            Await(vm.TranslateAsync()); window.UpdateLayout();
            Require(!vm.SourceExpanded && layout.ReadSourceExpanded() is null && speech.Calls.Count == 0, "Default layout or translation automatically persisted/started speech.");
            Click(window, "SpeakSourceButton");
            Wait(() => speech.Calls.Count == 1);
            var original = speech.Calls[^1];
            Require(original.Language == "zh" && original.Text == vm.Source && vm.SourceSpeechActionText == "停止朗读", "Chinese source did not use the source text and Chinese voice.");
            Click(window, "SpeakTranslationButton");
            Wait(() => speech.Calls.Count == 2 && original.Token.IsCancellationRequested);
            Require(speech.Calls[^1].Language == "en" && speech.Calls[^1].Text == vm.Translated && vm.SpeechActionText == "停止朗读" && vm.SourceSpeechActionText == "朗读原文", "Switching source to translation did not cancel or chose the wrong voice/text.");
            original.Pending.TrySetResult();
            var pause = Task.Delay(60); Await(pause);
            Require(vm.Speaking && vm.SpeechStatus.Contains("译文") && !vm.SpeechStatus.Contains("失败"), "Late cancelled source speech overwrote the active result.");
            Click(window, "OpenSourceReadingButton");
            var sourceReader = window.OwnedWindows.OfType<SourceReadingWindow>().Single();
            var previous = speech.Calls[^1];
            Click(sourceReader, "ReadingSourceSpeakButton");
            Wait(() => speech.Calls.Count == 3 && previous.Token.IsCancellationRequested);
            var readingCall = speech.Calls[^1]; sourceReader.Close();
            Wait(() => !vm.Speaking);
            Require(readingCall.Token.IsCancellationRequested && window.IsVisible, "Closing source reader did not stop only its playback.");
            Click(window, "ToggleSourceButton");
            Require(layout.ReadSourceExpanded() == true && vm.SourceExpanded, "Expanding original did not persist.");
            vm.OcrCommand.Execute(null); Wait(() => !vm.Busy && vm.OcrCommand.CanExecute(null));
            Await(vm.TranslateAsync());
            Require(vm.SourceExpanded, "Re-recognition or a new translation discarded saved expansion.");
            Require(File.ReadAllText(Path.Combine(folder, "reading-translation-defaults.json")) == defaultJson, "Saving layout changed translation defaults.");
            window.Close();
        }
        // Fresh stores/view models exercise durable choices for each translation mode.
        foreach (var mode in Enum.GetValues<TranslationMode>())
        {
            using var vm = Create(mode);
            var window = new TextResultWindow(vm); window.Show(); Wait(() => !vm.Busy && vm.Source.Length > 0);
            vm.Source = "English original before translation.";
            Await(vm.TranslateAsync()); window.UpdateLayout();
            Require(vm.SourceExpanded && vm.ShowSpeechButton && vm.ShowSourceSpeechButton, "A saved expanded layout or a source/result speaker is unavailable in a translation mode.");
            int before = speech.Calls.Count;
            Click(window, "SpeakSourceButton"); Wait(() => speech.Calls.Count > before);
            Require(speech.Calls[^1].Language == "en" && speech.Calls[^1].Text == vm.Source, "English original was not read as English.");
            before = speech.Calls.Count;
            Click(window, "SpeakTranslationButton"); Wait(() => speech.Calls.Count > before);
            Require(speech.Calls[^1].Language == "zh" && speech.Calls[^1].Text == vm.Translated, "Chinese translation was not read as Chinese.");
            Click(window, "SpeakTranslationButton"); Wait(() => !vm.Speaking);
            speech.Fail = true; Await(vm.ToggleSpeechAsync()); speech.Fail = false;
            Require(vm.SpeechStatus.Contains("Windows 中文语音") && vm.Translated == Translation.Chinese && window.IsVisible, "Missing Chinese voice discarded content or gave English-only guidance.");
            window.Close();
        }
        using (var vm = Create(TranslationMode.Online))
        {
            var window = new TextResultWindow(vm); window.Show(); Wait(() => !vm.Busy && vm.Source.Length > 0);
            Await(vm.TranslateAsync()); Click(window, "ToggleSourceButton");
            Require(!vm.SourceExpanded && new TextReadingPreferencesStore(path).ReadSourceExpanded() == false, "Collapsing original did not persist.");
            window.Close();
        }
        using (var vm = Create(TranslationMode.Offline))
        {
            var window = new TextResultWindow(vm); window.Show(); Wait(() => !vm.Busy && vm.Source.Length > 0); window.UpdateLayout();
            Require(vm.SourceExpanded && ((TextBox)window.FindName("RecognizedText")).IsVisible, "Saved collapsed translation hid OCR-only output.");
            Await(vm.TranslateAsync());
            Require(!vm.SourceExpanded, "Fresh result did not restore the saved collapsed choice after OCR.");
            window.Close();
        }
        File.WriteAllText(path, "{\"SourceExpanded\":\"invalid\"}");
        using (var vm = Create(TranslationMode.Online))
        {
            Require(vm.ReadingLayoutStatus.Contains("失败"), "Corrupt layout preference was silently accepted.");
            var window = new TextResultWindow(vm); window.Show(); Wait(() => !vm.Busy && vm.Source.Length > 0);
            Await(vm.TranslateAsync()); Click(window, "ToggleSourceButton");
            Require(new TextReadingPreferencesStore(path).ReadSourceExpanded() == true, "A new user choice did not repair corrupt layout preferences.");
            window.Close();
        }
        using (var vm = Create(TranslationMode.Online, new RefusedPreferences()))
        {
            var window = new TextResultWindow(vm); window.Show(); Wait(() => !vm.Busy && vm.Source.Length > 0);
            Await(vm.TranslateAsync()); Click(window, "ToggleSourceButton");
            Require(vm.SourceExpanded && vm.ReadingLayoutStatus.Contains("失败") && window.IsVisible, "A failed layout write closed the window or reversed the current layout.");
            window.Close();
        }
        Require(!File.ReadAllText(path).Contains("原文") && translator.Calls > 0, "Reading preferences retained source content.");
        Console.WriteLine("PASS actual bilingual source/result speakers in online/offline/basic modes; source-reader playback, switching/stop, missing Chinese voice, stale completion; durable expand/collapse, re-recognition, OCR-only visibility, corrupt/write-failed preferences and independent translation defaults.");
    }
    private static void Click(Window window, string name)
    {
        var button = (Button)window.FindName(name);
        if (button.Command is { } command) { Require(command.CanExecute(null), "Disabled command: " + name); command.Execute(null); }
        else button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    private static void Wait(Func<bool> condition) => TextWindowChecks.PumpUntil(condition, TimeSpan.FromSeconds(6));
    private static void Await(Task task) { Wait(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Speech : ITextSpeechService
    {
        public sealed record Call(string Text, string Language, CancellationToken Token, TaskCompletionSource Pending);
        public readonly List<Call> Calls = []; public bool Fail;
        public Task SpeakAsync(string text, string language, CancellationToken token)
        {
            if (Fail) throw new SpeechUnavailableException(language);
            var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Calls.Add(new(text, language, token, pending)); return pending.Task.WaitAsync(token);
        }
    }
    private sealed class Translation : ITranslationService
    {
        public const string Chinese = "这是完整的中文译文。"; public int Calls;
        public Task<string> TranslateAsync(string text, string direction, CancellationToken token)
        { Calls++; return Task.FromResult(direction == "zh-en" ? "This is the complete English translation." : Chinese); }
    }
    private sealed class RefusedPreferences : ITextReadingPreferencesStore
    {
        public bool? ReadSourceExpanded() => null;
        public void SaveSourceExpanded(bool expanded) => throw new UnauthorizedAccessException("PRIVATE-PREFERENCE-PATH");
    }
}
