using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Snip.App.ViewModels;
using Snip.App.Views;
using Snip.Core;
using Snip.Translation;

namespace Snip.Windows.Checks;

internal static class VocabularySpeechChecks
{
    public static void Run(string folder, BitmapSource image)
    {
        var speech = new Speech(); var translation = new Translation();
        using var vm = new TextResultViewModel(image, new TextWindowChecks.FakeOcr(), new ModelStore(Path.Combine(folder, "vocabulary-no-models")),
            translation, onlineTranslator: translation, initialPreferences: new(TranslationMode.Online), speech: speech);
        var window = new TextResultWindow(vm); window.Show();
        Wait(() => !vm.Busy && vm.Source.Length > 0);
        Await(vm.TranslateAsync());
        // Use the actual notes tab, speaker buttons and reading window rather than invoking a text selector directly.
        ((TabItem)window.FindName("LanguageNotesTab")).IsSelected = true; window.UpdateLayout();
        Click(window, "SpeakTranslationButton"); Wait(() => speech.Calls.Count == 1);
        var words = speech.Calls[^1];
        Require(words.Text == "capture. keep" && words.Language == "en" && vm.SpeechStatus.Contains("词汇"), "Vocabulary tab still speaks translation, annotations, or the wrong language.");
        Click(window, "OpenReadingButton");
        var reading = window.OwnedWindows.OfType<TranslationReadingWindow>().Single(); reading.UpdateLayout();
        var readingTabs = (TabControl)reading.FindName("ReadingTabs");
        Require(readingTabs.SelectedIndex == 1 && ((TextBox)reading.FindName("ReadingNotes")).IsVisible, "Reader opened on a different speech context.");
        Click(reading, "ReadingSpeakButton"); Wait(() => !vm.Speaking);
        Require(words.Token.IsCancellationRequested, "Reader speaker failed to stop vocabulary playback.");
        readingTabs.SelectedIndex = 0; reading.UpdateLayout();
        Require(vm.ResultTab == 0 && ((TextBox)window.FindName("TranslatedText")).IsVisible, "Reader and workbench tabs are not synchronized.");
        Click(reading, "ReadingSpeakButton"); Wait(() => speech.Calls.Count == 2);
        var body = speech.Calls[^1]; Require(body.Text == translation.Result.Translation && body.Language == "zh", "Full-result tab did not read the complete Chinese translation.");
        ((TabItem)window.FindName("LanguageNotesTab")).IsSelected = true; Wait(() => !vm.Speaking);
        Require(body.Token.IsCancellationRequested && speech.Calls.Count == 2 && vm.SpeechActionText == "朗读词汇", "Tab switch kept reading the old result or automatically uploaded new speech.");
        Click(reading, "ReadingSpeakButton"); Wait(() => speech.Calls.Count == 3);
        Require(speech.Calls[^1].Text == words.Text, "Reader vocabulary speaker still reads translation.");
        Click(window, "SpeakSourceButton"); Wait(() => speech.Calls.Count == 4);
        var source = speech.Calls[^1];
        readingTabs.SelectedIndex = 0; window.UpdateLayout();
        Require(!source.Token.IsCancellationRequested && source.Text == vm.Source, "Result tab switch stopped or changed original speech.");
        Click(window, "SpeakSourceButton"); Wait(() => !vm.Speaking);
        // A new response must replace the old vocabulary, including pinyin-only and absent annotations.
        translation.Result = new("新的中文译文", "xīn de zhōng wén yì wén", []);
        Await(vm.TranslateAsync());
        ((TabItem)window.FindName("LanguageNotesTab")).IsSelected = true; window.UpdateLayout();
        int before = speech.Calls.Count;
        Require(!vm.ShowSpeechButton && !vm.SpeakTranslationCommand.CanExecute(null), "Pinyin-only notes retain stale vocabulary or silently fall back to translation.");
        Await(vm.ToggleSpeechAsync()); Require(speech.Calls.Count == before, "Notes without vocabulary sent a speech request.");
        translation.Result = new("New English translation.", "", [new("fresh", "/freʃ/", "形容词", "新的")]);
        Await(vm.TranslateAsync());
        Require(vm.ResultTab == 0 && vm.ShowSpeechButton, "New translation did not restore full-result speech.");
        readingTabs.SelectedIndex = 1; reading.UpdateLayout();
        Click(reading, "ReadingSpeakButton"); Wait(() => speech.Calls.Count == before + 1);
        Require(speech.Calls[^1].Text == "fresh", "New translation reused previous vocabulary.");
        var closing = speech.Calls[^1]; reading.Close(); Wait(() => !vm.Speaking);
        Require(closing.Token.IsCancellationRequested && window.IsVisible, "Closing reader retained vocabulary speech.");
        translation.Result = new("No annotations.", "", []); Await(vm.TranslateAsync());
        Require(!vm.HasLanguageNotes && vm.ResultTab == 0 && vm.ShowSpeechButton, "Missing annotations broke ordinary translation speech.");
        window.Close();
        Console.WriteLine("PASS vocabulary speech: actual workbench/reader tabs, English words only, Chinese full result, shared stop/switch, original unchanged, pinyin-only no request, replacement/absent notes and close cancellation.");
    }
    private static void Click(Window window, string name)
    {
        var button = (Button)window.FindName(name);
        if (button.Command is { } command) { Require(command.CanExecute(null), "Disabled speech command: " + name); command.Execute(null); }
        else button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    private static void Wait(Func<bool> condition) => TextWindowChecks.PumpUntil(condition, TimeSpan.FromSeconds(5));
    private static void Await(Task task) { Wait(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Translation : IAnnotatedTranslationService
    {
        public AnnotatedTranslation Result = new("捕捉想法，保留细节。", "bǔ zhuō xiǎng fǎ", [new("capture", "/ˈkæptʃər/", "动词", "捕捉"), new("keep", "/kiːp/", "动词", "保留")]);
        public Task<string> TranslateAsync(string text, string direction, CancellationToken token) => Task.FromResult(Result.Translation);
        public Task<AnnotatedTranslation> TranslateWithDetailsAsync(string text, string direction, IProgress<TranslationProgress>? progress, CancellationToken token) => Task.FromResult(Result);
    }
    private sealed record Call(string Text, string Language, CancellationToken Token);
    private sealed class Speech : ITextSpeechService
    {
        public readonly List<Call> Calls = [];
        public Task SpeakAsync(string text, string language, CancellationToken token)
        { Calls.Add(new(text, language, token)); return Task.Delay(Timeout.Infinite, token); }
    }
}
