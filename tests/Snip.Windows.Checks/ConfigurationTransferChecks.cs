using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Snip.App.ViewModels;
using Snip.App.Views;
using Snip.Core;
using Snip.Infrastructure;

namespace Snip.Windows.Checks;

internal static class ConfigurationTransferChecks
{
    private const string Password = "PUBLIC 配置迁移 Password 12345";
    public static int Run()
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var context = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        string folder = Path.Combine(Path.GetTempPath(), "Snip 配置迁移 " + Guid.NewGuid().ToString("N"));
        string work = Path.Combine(folder, "work"), home = Path.Combine(folder, "home"), archive = Path.Combine(folder, "配置.snipconfig");
        Directory.CreateDirectory(work); Directory.CreateDirectory(home);
        string stage = "prepare public settings";
        using var watchdog = new System.Threading.Timer(_ => { Console.WriteLine("::error title=Configuration transfer deadline::" + stage); Environment.Exit(1); }, null, TimeSpan.FromMinutes(2), Timeout.InfiniteTimeSpan);
        try
        {
            var office = new SettingsConfigurationTransfer(work); var house = new SettingsConfigurationTransfer(home);
            var translation = new OnlineTranslationSettingsStore(Path.Combine(work, "online-translation.json"));
            translation.Save(new("PUBLIC-EMPTY", "https://empty.example.com/v1", "empty-model"), null);
            translation.Save(new("PUBLIC-TRANSLATION", "https://public.example.com/v1", "PUBLIC-MODEL", true, VisionModel: "PUBLIC-VISION"), "PUBLIC-WORK-TRANSLATION-KEY");
            var speech = new OnlineSpeechSettingsStore(translation, Path.Combine(work, "online-speech.json"));
            speech.Save(new("PUBLIC-SHARED", SpeechProtocol.OpenAi, "https://public.example.com/tts", "shared-model", "shared-voice", TranslationProfile: "PUBLIC-TRANSLATION"), null, SpeechMode.System);
            speech.Save(new("PUBLIC-VOICE", SpeechProtocol.OpenAi, "https://voice.example.com/tts", "voice-model", "voice", 1.25), "PUBLIC-WORK-VOICE-KEY", SpeechMode.Online);
            new TranslationPreferencesStore(Path.Combine(work, "translation-preferences.json")).Save(new(TranslationMode.Basic, "auto"));
            new OcrPreferencesStore(Path.Combine(work, "ocr-preferences.json")).Save(OcrMode.EnhancedOffline);
            new TextReadingPreferencesStore(Path.Combine(work, "text-reading-preferences.json")).SaveSourceExpanded(true);
            var captureKey = new HotkeyGesture(3, 0x79); var pinKeys = new PinHotkeySettings(Restore: new(3, 0x78));
            new HotkeySettingsStore(Path.Combine(work, "hotkey.json")).Save(captureKey);
            new PinHotkeySettingsStore(Path.Combine(work, "pin-hotkeys.json")).Save(pinKeys);
            File.WriteAllText(Path.Combine(work, "screenshot-private.txt"), "PUBLIC-DO-NOT-TRANSFER-SCREENSHOT");
            File.WriteAllText(Path.Combine(work, "account-sync.json"), "PUBLIC-DO-NOT-TRANSFER-SESSION");
            var destination = new OnlineTranslationSettingsStore(Path.Combine(home, "online-translation.json"));
            destination.Save(new("PUBLIC-HOME", "https://home.example.com/v1", "HOME-MODEL"), "PUBLIC-HOME-KEY");
            var destinationSpeech = new OnlineSpeechSettingsStore(destination, Path.Combine(home, "online-speech.json"));
            destinationSpeech.Save(new("PUBLIC-HOME-VOICE", SpeechProtocol.OpenAi, "https://home.example.com/tts", "home-model", "home"), "PUBLIC-HOME-VOICE-KEY", SpeechMode.System);
            File.WriteAllText(Path.Combine(home, "unrelated.json"), "PUBLIC-KEEP-LOCAL");
            using var journal = new DiagnosticJournal(Path.Combine(folder, "diagnostics"));
            var vm = new ConfigurationTransferViewModel(office, () => archive, () => archive, () => true, () => { }, e => journal.Record("configuration-transfer", "failed", error:e));
            var window = new ConfigurationTransferWindow(vm); window.Show(); window.UpdateLayout();
            stage = "password mismatch and actual export controls";
            Set(window, "ExportPasswordBox", Password); Set(window, "ExportConfirmationBox", "PUBLIC mismatch");
            Click(window, "ExportConfigurationButton"); Wait(() => !vm.Busy);
            Require(!File.Exists(archive) && vm.Status.Contains("不一致"), "Export ignored password confirmation.");
            Set(window, "ExportConfirmationBox", Password);
            Preview(window, "47-configuration-transfer");
            int ticks = 0; var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(10), DispatcherPriority.Background, (_, _) => ticks++, app.Dispatcher); timer.Start();
            Click(window, "ExportConfigurationButton"); Wait(() => vm.Status.StartsWith("已导出", StringComparison.Ordinal) && !vm.Busy); timer.Stop();
            Require(ticks > 0 && !vm.Password.Any() && !vm.Confirmation.Any() && ((PasswordBox)window.FindName("ExportPasswordBox")).Password.Length == 0, "Export blocked dispatcher or retained password controls.");
            byte[] encrypted = File.ReadAllBytes(archive);
            string binary = Encoding.UTF8.GetString(encrypted);
            Require(!binary.Contains("PUBLIC-WORK") && !binary.Contains(Password) && !binary.Contains("public.example.com"), "Export file contains plaintext credentials.");
            byte[] clear = EncryptedConfigurationArchive.Decrypt(encrypted, Password);
            try { Require(!Encoding.UTF8.GetString(clear).Contains("PUBLIC-DO-NOT-TRANSFER"), "Unrelated image/session entered the archive."); }
            finally { CryptographicOperations.ZeroMemory(clear); }
            Console.WriteLine("PASS real export controls: password confirmation, encrypted portable file, responsive dispatcher, password clearing and configuration-only whitelist.");
            window.Close();

            stage = "wrong password and preview without mutation";
            int applied = 0;
            var homeVm = new ConfigurationTransferViewModel(house, () => archive, () => archive,
                () => !app.Windows.OfType<OnlineSpeechSettingsWindow>().Any(), () => applied++, e => journal.Record("configuration-transfer", "failed", error:e));
            var homeWindow = new ConfigurationTransferWindow(homeVm); homeWindow.Show(); homeWindow.UpdateLayout();
            string original = house.Capture().Fingerprint;
            Set(homeWindow, "ImportPasswordBox", "PUBLIC wrong password"); Click(homeWindow, "PreviewConfigurationButton");
            Wait(() => !homeVm.Busy && homeVm.Status.Contains("密码不正确"));
            Require(house.Capture().Fingerprint == original && !homeVm.ApplyCommand.CanExecute(null), "Wrong password changed destination or enabled apply.");
            Set(homeWindow, "ImportPasswordBox", Password); Click(homeWindow, "PreviewConfigurationButton");
            Wait(() => homeVm.ApplyCommand.CanExecute(null));
            Require(house.Capture().Fingerprint == original && homeVm.Preview.Contains("2 组") && homeVm.ImportPassword.Length == 0, "Preview applied before confirmation or lost profiles.");
            Preview(homeWindow, "48-configuration-import-preview");
            using (var speechVm = new OnlineSpeechSettingsViewModel(destinationSpeech, destination, _ => throw new InvalidOperationException("No provider expected."), new NoAudio()))
            {
                var settings = new OnlineSpeechSettingsWindow(speechVm); settings.Show(); speechVm.Model = "PUBLIC-UNSAVED-DRAFT";
                Click(homeWindow, "ApplyConfigurationButton");
                Require(applied == 0 && house.Capture().Fingerprint == original && speechVm.Model == "PUBLIC-UNSAVED-DRAFT" && homeVm.Status.Contains("关闭"), "Import overwrote an open settings editor.");
                settings.Close();
            }
            Click(homeWindow, "ApplyConfigurationButton");
            Require(applied == 1 && homeVm.Status.Contains("导入成功"), "Explicit import did not complete.");
            Require(destination.ReadCredentials().ApiKey == "PUBLIC-WORK-TRANSLATION-KEY" && destination.Read()!.Model == "PUBLIC-MODEL" && destination.Read()!.VisionModel == "PUBLIC-VISION" && destination.ReadProfiles().Count == 2, "Translation, empty-key profile or vision model missing.");
            Require(destinationSpeech.ReadCredentials().ApiKey == "PUBLIC-WORK-VOICE-KEY" && destinationSpeech.ReadCredentials("PUBLIC-SHARED").ApiKey == "PUBLIC-WORK-TRANSLATION-KEY" && destinationSpeech.ReadMode() == SpeechMode.Online && destinationSpeech.Read()!.Speed == 1.25, "Voice/shared-key/speed/default missing.");
            Require(new TranslationPreferencesStore(Path.Combine(home, "translation-preferences.json")).Read()!.Mode == TranslationMode.Basic && new OcrPreferencesStore(Path.Combine(home, "ocr-preferences.json")).Read() == OcrMode.EnhancedOffline && new TextReadingPreferencesStore(Path.Combine(home, "text-reading-preferences.json")).ReadSourceExpanded() == true, "Mode/layout preferences missing.");
            Require(new HotkeySettingsStore(Path.Combine(home, "hotkey.json")).Read() == captureKey && new PinHotkeySettingsStore(Path.Combine(home, "pin-hotkeys.json")).Read() == pinKeys, "Hotkeys missing.");
            Require(File.ReadAllText(Path.Combine(home, "unrelated.json")) == "PUBLIC-KEEP-LOCAL" && !File.Exists(Path.Combine(home, "account-sync.json")) && !File.Exists(Path.Combine(home, "screenshot-private.txt")), "Import modified unrelated files.");
            Require(File.ReadAllText(Path.Combine(work, "online-translation.json")) != File.ReadAllText(Path.Combine(home, "online-translation.json")), "Imported DPAPI bytes were not re-protected for the destination.");
            Console.WriteLine("PASS actual preview/apply controls, unsaved-editor guard, destination DPAPI protection, independent/shared keys, empty-key profiles, models/voices/speed, OCR/translation/layout and hotkeys.");

            stage = "tampering and stale local settings";
            var corrupted = encrypted.ToArray(); corrupted[^1] ^= 1; File.WriteAllBytes(archive, corrupted);
            string accepted = house.Capture().Fingerprint;
            Set(homeWindow, "ImportPasswordBox", Password); Click(homeWindow, "PreviewConfigurationButton"); Wait(() => !homeVm.Busy && homeVm.Status.Contains("损坏"));
            Require(!homeVm.ApplyCommand.CanExecute(null) && house.Capture().Fingerprint == accepted, "Tampering changed local settings.");
            File.WriteAllBytes(archive, encrypted);
            Set(homeWindow, "ImportPasswordBox", Password); Click(homeWindow, "PreviewConfigurationButton"); Wait(() => homeVm.ApplyCommand.CanExecute(null));
            destination.Save(destination.Read()! with { Model = "PUBLIC-NEW-LOCAL-EDIT" }, null);
            Click(homeWindow, "ApplyConfigurationButton");
            Require(applied == 1 && homeVm.Status.Contains("新修改") && destination.Read()!.Model == "PUBLIC-NEW-LOCAL-EDIT", "Stale preview overwrote new local settings.");
            Console.WriteLine("PASS wrong password/tampering preserve complete local configuration; stale preview cannot overwrite a new local edit.");

            stage = "Windows locked-file rollback and interrupted import recovery";
            var before = house.Capture(); var prepared = house.Decrypt(encrypted, Password);
            using (var locked = new FileStream(Path.Combine(home, "online-speech.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { house.Apply(prepared, before.Fingerprint); throw new InvalidOperationException("Locked destination accepted."); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                Require(File.Exists(Path.Combine(home, "configuration-transfer-transaction.json")), "Interrupted transaction has no recovery journal.");
            }
            new SettingsConfigurationTransfer(home).RecoverInterruptedImport();
            Require(house.Capture().Fingerprint == before.Fingerprint && destination.ReadCredentials().ApiKey == "PUBLIC-WORK-TRANSLATION-KEY" && destination.Read()!.Model == "PUBLIC-NEW-LOCAL-EDIT", "Recovery failed to restore full original settings and keys.");
            Require(!File.Exists(Path.Combine(home, "configuration-transfer-transaction.json")), "Successful recovery retained journal.");
            Console.WriteLine("PASS actual Windows replacement failure retains protected rollback journal; fresh service restores all seven original files and keys.");

            stage = "closed window discards late decryption";
            Set(homeWindow, "ImportPasswordBox", Password); Click(homeWindow, "PreviewConfigurationButton");
            Require(homeVm.Busy, "Decrypt did not enter busy state."); homeWindow.Close();
            Wait(() => !homeVm.Busy);
            Require(!homeVm.ApplyCommand.CanExecute(null) && homeVm.Password.Length == 0 && homeVm.ImportPassword.Length == 0 && house.Capture().Fingerprint == before.Fingerprint, "Closed window retained a late import or password.");
            journal.FlushAsync().GetAwaiter().GetResult();
            string log = File.ReadAllText(Path.Combine(folder, "diagnostics", "events.jsonl"));
            Require(!log.Contains(Password) && !log.Contains("PUBLIC-WORK") && !log.Contains("public.example.com"), "Diagnostic log disclosed credentials or service endpoints.");
            Require(!Directory.EnumerateFiles(work, "*", SearchOption.AllDirectories).Any(p => p.EndsWith(".tmp", StringComparison.Ordinal)), "Export left temporary files.");
            Console.WriteLine("PASS closing cancels late preview/application, clears passwords and leaves configuration unchanged; diagnostics contain no keys/passwords/endpoints. No paid service contacted.");
            return 0;
        }
        catch (Exception error)
        {
            Console.WriteLine("::error title=Configuration transfer UI::" + stage + ": " + error.GetType().Name + ": " + error.Message.Replace("%", "%25").Replace("\n", "%0A").Replace("\r", "%0D")); return 1;
        }
        finally
        {
            foreach (Window window in app.Windows.Cast<Window>().ToArray()) window.Close();
            SynchronizationContext.SetSynchronizationContext(context); Directory.Delete(folder, true);
        }
    }
    private static void Set(Window window, string name, string value) => ((PasswordBox)window.FindName(name)).Password = value;
    private static void Click(Window window, string name)
    {
        var button = (Button)window.FindName(name);
        if (button.Command is { } command) { Require(command.CanExecute(null), "Disabled command: " + name); command.Execute(null); }
        else button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    private static void Wait(Func<bool> predicate) => TextWindowChecks.PumpUntil(predicate, TimeSpan.FromSeconds(15));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Preview(Window window, string name)
    {
        window.UpdateLayout(); var a = window.PointToScreen(new Point()); var b = window.PointToScreen(new Point(window.ActualWidth, window.ActualHeight));
        var capture = new DesktopCaptureService(); var bounds = PixelRect.FromPoints(new(a.X, a.Y), new(b.X, b.Y)).Intersect(capture.GetDisplays().First(d => d.Primary).Bounds);
        Directory.CreateDirectory("artifacts/validation/ui-previews");
        new ImageExportService().SavePng(capture.CaptureRegion(bounds), "artifacts/validation/ui-previews/" + name + ".png");
    }
    private sealed class NoAudio : ISpeechAudioPlayer { public Task PlayAsync(SpeechAudio audio, double speed, CancellationToken token) => throw new InvalidOperationException("No provider expected."); }
}
