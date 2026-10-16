using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Snip.App.ViewModels;
using Snip.App.Views;
using Snip.Core;
using Snip.Infrastructure;
using Snip.Translation;
using Windows.Storage.Streams;

namespace Snip.Windows.Checks;

internal static class OnlineSpeechSettingsChecks
{
    public static void Run(string folder, BitmapSource image)
    {
        string path = Path.Combine(folder, "ai-speech.json");
        var translation = new OnlineTranslationSettingsStore(Path.Combine(folder, "speech-shared-translation.json"));
        translation.Save(new("同平台翻译", "https://api.openai.com/v1", "text-model"), "PUBLIC-SHARED-KEY");
        string originalTranslation = File.ReadAllText(Path.Combine(folder, "speech-shared-translation.json"));
        var store = new OnlineSpeechSettingsStore(translation, path);
        Require(store.ReadMode() == SpeechMode.System && store.ReadProfiles().Count == 0, "Fresh speech settings enabled online mode.");
        var synth = new Synth(); var player = new Player();
        using var vm = new OnlineSpeechSettingsViewModel(store, translation, _ => synth, player);
        var window = new OnlineSpeechSettingsWindow(vm); window.Show(); window.UpdateLayout();
        Require(vm.Providers.Count >= 4 && synth.Calls == 0 && player.Calls == 0, "Opening settings performs network/playback or lacks presets.");
        ((PasswordBox)window.FindName("SpeechApiKeyBox")).Password = "PUBLIC-VOICE-KEY";
        ((ComboBox)window.FindName("SpeechDefaultModeBox")).SelectedValue = SpeechMode.Online;
        Click(window, "SaveSpeechButton");
        Require(store.ReadMode() == SpeechMode.Online && store.ReadCredentials().ApiKey == "PUBLIC-VOICE-KEY" && ((PasswordBox)window.FindName("SpeechApiKeyBox")).Password.Length == 0, "Actual settings did not save mode/key or clear password control.");
        string json = File.ReadAllText(path);
        Require(!json.Contains("PUBLIC-VOICE-KEY") && !json.Contains("PUBLIC-SHARED-KEY"), "Speech keys saved as plaintext.");
        var reopened = new OnlineSpeechSettingsStore(translation, path);
        Require(reopened.ReadMode() == SpeechMode.Online && reopened.ReadCredentials().Settings.Voice == "coral", "Speech preferences failed a fresh store read.");
        int calls = synth.Calls; Click(window, "SpeechPreviewButton"); Wait(() => synth.Calls > calls && vm.PreviewCommand.CanExecute(null));
        Require(synth.Text.Contains("你好") && synth.Text.Contains("Hello") && !synth.Text.Contains("PUBLIC-SOURCE") && player.Calls == 1 && File.ReadAllText(path) == json, "Preview sent OCR text, did not play, or silently saved.");
        ((ComboBox)window.FindName("SpeechTranslationKeyBox")).SelectedItem = "同平台翻译";
        Click(window, "SaveSpeechButton");
        Require(store.ReadCredentials().ApiKey == "PUBLIC-SHARED-KEY" && File.ReadAllText(Path.Combine(folder, "speech-shared-translation.json")) == originalTranslation, "Shared voice key modified translation settings.");
        string before = File.ReadAllText(path); vm.Endpoint = "https://another.example.com/tts"; Click(window, "SaveSpeechButton");
        Require(vm.Status.Contains("同一服务") && File.ReadAllText(path) == before, "Cross-domain key reuse was saved.");
        vm.Endpoint = "https://api.openai.com/v1/audio/speech";
        vm.CustomName = "自定义语音"; vm.AddCommand.Execute(null);
        Require(vm.Provider == "自定义语音" && vm.Model.Length == 0 && vm.Voice.Length == 0, "Custom profile did not expose editable fields.");
        vm.Endpoint = "https://speech.example.com/tts"; vm.Model = "custom-model"; vm.Voice = "custom-voice"; vm.Speed = 1.25;
        ((PasswordBox)window.FindName("SpeechApiKeyBox")).Password = "PUBLIC-CUSTOM-KEY"; Click(window, "SaveSpeechButton");
        Require(store.ReadCredentials().Settings.Model == "custom-model" && store.ReadCredentials().Settings.Speed == 1.25, "Custom speech parameters not persisted.");
        ((ComboBox)window.FindName("SpeechProviderBox")).SelectedItem = "OpenAI";
        Require(vm.Voice == "coral" && ((PasswordBox)window.FindName("SpeechApiKeyBox")).Password.Length == 0, "Switching profiles leaked a password or lost prior fields.");
        window.Width = window.MinWidth; window.Height = window.MinHeight; window.UpdateLayout();
        Require(((Button)window.FindName("SaveSpeechButton")).IsVisible, "Compact speech settings lost Save.");
        // Preview cancellation and closing do not save drafts or keep background playback alive.
        synth.Block = true; calls = synth.Calls; Click(window, "SpeechPreviewButton"); Wait(() => synth.Calls > calls);
        window.Close(); Wait(() => synth.Token.IsCancellationRequested);
        store.SaveMode(SpeechMode.System);
        void SyncModes()
        {
            foreach (var shown in Application.Current.Windows.OfType<TextResultWindow>())
                if (shown.DataContext is TextResultViewModel text) text.ReloadVoicePreferences();
        }
        using var second = new TextResultViewModel(image, new TextWindowChecks.FakeOcr(), new ModelStore(Path.Combine(folder, "speech-no-models-second")),
            new TextWindowChecks.FakeTranslation(), speech: new ConfiguredTextSpeechService(store, new SystemSpeech(), _ => synth, player), voiceModeSaved: SyncModes);
        var secondWindow = new TextResultWindow(second); secondWindow.Show(); Wait(() => !second.Busy && second.Source.Length > 0);
        using var result = new TextResultViewModel(image, new TextWindowChecks.FakeOcr(), new ModelStore(Path.Combine(folder, "speech-no-models")),
            new TextWindowChecks.FakeTranslation(), speech: new ConfiguredTextSpeechService(store, new SystemSpeech(), _ => synth, player), configureSpeech: () => { }, voiceModeSaved: SyncModes);
        var resultWindow = new TextResultWindow(result); resultWindow.Show(); Wait(() => !result.Busy && result.Source.Length > 0);
        Require(result.SupportsSpeechModes && result.VoiceMode == SpeechMode.System, "Workbench has no configurable voice mode.");
        result.VoiceMode = SpeechMode.Online;
        Require(second.VoiceMode == SpeechMode.Online && new OnlineSpeechSettingsStore(translation, path).ReadMode() == SpeechMode.Online, "Workbench voice selection did not persist.");
        synth.Block = true; calls = synth.Calls;
        var otherPlaying = second.ToggleSpeechAsync(original: true); Wait(() => synth.Calls > calls && second.Speaking);
        var otherToken = synth.Token;
        result.VoiceMode = SpeechMode.System; Await(otherPlaying);
        Require(otherToken.IsCancellationRequested && !second.Speaking && second.VoiceMode == SpeechMode.System, "Changing shared mode did not cancel another workbench's old playback.");
        result.VoiceMode = SpeechMode.Online;
        synth.Block = false; synth.Fail = true; Await(result.ToggleSpeechAsync(original: true));
        Require(resultWindow.IsVisible && result.Source == "Hello world" && result.SpeechStatus.Contains("PUBLIC-CONTROLLED"), "Online voice failure hid content or lost controlled guidance.");
        result.VoiceMode = SpeechMode.System; Require(second.VoiceMode == SpeechMode.System, "Other workbench still displays the previous speech mode."); Await(result.ToggleSpeechAsync(original: true));
        Require(result.SpeechStatus.Contains("完成"), "Explicit manual system fallback failed.");
        resultWindow.Close(); secondWindow.Close();
        NativeMemoryAudio();
        Require(!File.ReadAllText(path).Contains("PUBLIC-SOURCE") && !File.ReadAllText(path).Contains("PUBLIC-CUSTOM-KEY"), "Speech settings retain user text or plaintext key.");
        Console.WriteLine("PASS actual AI speech settings: four presets/custom fields, DPAPI, default mode, shared key origin gate, preview/no autosave/cancel, independent translation settings, workbench/multiple-window mode sync, manual fallback and real WinRT memory audio roundtrip.");
    }
    private static void NativeMemoryAudio()
    {
        byte[] payload = Enumerable.Range(0, 512).Select(i => (byte)i).ToArray();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var opening = WindowsSpeechAudioPlayer.CreateStreamAsync(new(payload, "audio/wav"), deadline.Token); Await(opening);
        using var stream = opening.GetAwaiter().GetResult();
        Require(stream.Size == (ulong)payload.Length, "Native audio memory stream is closed or incomplete.");
        using var reader = new DataReader(stream); var loading = reader.LoadAsync((uint)payload.Length).AsTask(deadline.Token); Await(loading);
        var actual = new byte[payload.Length]; reader.ReadBytes(actual); reader.DetachStream();
        Require(actual.SequenceEqual(payload), "Native audio bytes changed or were released before playback.");
    }
    private static void Click(Window window, string name)
    {
        var button = (Button)window.FindName(name); Require(button.Command!.CanExecute(null), "Disabled command: " + name); button.Command.Execute(null);
    }
    private static void Wait(Func<bool> condition) => TextWindowChecks.PumpUntil(condition, TimeSpan.FromSeconds(6));
    private static void Await(Task task) { Wait(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Synth : IOnlineSpeechSynthesizer
    {
        public int Calls; public string Text = ""; public CancellationToken Token; public bool Block, Fail;
        public async Task<SpeechAudio> SynthesizeAsync(string text, string language, CancellationToken token)
        {
            Calls++; Text = text; Token = token;
            if (Fail) throw new SpeechServiceException("PUBLIC-CONTROLLED 在线语音不可用，请手动切换。");
            if (Block) await Task.Delay(Timeout.Infinite, token);
            return new([1, 2, 3], "audio/wav");
        }
    }
    private sealed class Player : ISpeechAudioPlayer { public int Calls; public Task PlayAsync(SpeechAudio audio, double speed, CancellationToken token) { token.ThrowIfCancellationRequested(); Calls++; return Task.CompletedTask; } }
    private sealed class SystemSpeech : ITextSpeechService { public Task SpeakAsync(string text, string language, CancellationToken token) => Task.CompletedTask; }
}
