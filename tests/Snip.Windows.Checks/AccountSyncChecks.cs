using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Snip.App.ViewModels;
using Snip.App.Views;
using Snip.Core;
using Snip.Infrastructure;

namespace Snip.Windows.Checks;

internal static class AccountSyncChecks
{
    private const string User = "public-owner", Password = "PUBLIC Account Password 12345", Invite = "PUBLIC-REGISTRATION-INVITE-123456789";
    public static int Run()
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        Ui.EnsureTheme();
        string folder = Path.GetFullPath("artifacts/validation/account-sync-public");
        if (Directory.Exists(folder)) Directory.Delete(folder, true); Directory.CreateDirectory(folder);
        string work = Path.Combine(folder, "work"), home = Path.Combine(folder, "home"); Directory.CreateDirectory(work); Directory.CreateDirectory(home);
        string stage = "start public server";
        AccountSyncService? office = null, house = null, restarted = null;
        using var watchdog = new System.Threading.Timer(_ => { Console.WriteLine("::error title=Account sync deadline::" + stage); Environment.Exit(1); }, null, TimeSpan.FromMinutes(2), Timeout.InfiniteTimeSpan);
        using var server = new Server(Path.Combine(folder, "server"));
        try
        {
            Await(server.StartAsync());
            var translation = new OnlineTranslationSettingsStore(Path.Combine(work, "online-translation.json"));
            translation.Save(new("PUBLIC-EMPTY", "https://empty.example.com/v1", "empty-model"), null);
            translation.Save(new("PUBLIC-TRANSLATION", "https://public.example.com/v1", "PUBLIC-MODEL", true, VisionModel: "PUBLIC-VISION"), "PUBLIC-WORK-TRANSLATION-KEY");
            var voice = new OnlineSpeechSettingsStore(translation, Path.Combine(work, "online-speech.json"));
            voice.Save(new("PUBLIC-SHARED", SpeechProtocol.OpenAi, "https://public.example.com/tts", "shared-model", "shared-voice", TranslationProfile: "PUBLIC-TRANSLATION"), null, SpeechMode.System);
            voice.Save(new("PUBLIC-VOICE", SpeechProtocol.OpenAi, "https://voice.example.com/tts", "voice-model", "voice", 1.25), "PUBLIC-WORK-VOICE-KEY", SpeechMode.Online);
            new TranslationPreferencesStore(Path.Combine(work, "translation-preferences.json")).Save(new(TranslationMode.Online, "auto"));
            new OcrPreferencesStore(Path.Combine(work, "ocr-preferences.json")).Save(OcrMode.Online);
            File.WriteAllText(Path.Combine(work, "private-screenshot.txt"), "PUBLIC-SHOULD-NOT-UPLOAD");
            using var journal = new DiagnosticJournal(Path.Combine(folder, "diagnostics"));
            office = new(work, app.Dispatcher, () => true, () => { }, journal); Await(office.StartAsync());
            var officeVm = new AccountSyncViewModel(office, app.Dispatcher); var officeWindow = new AccountSyncWindow(officeVm); officeWindow.Show(); officeWindow.UpdateLayout();
            officeVm.Server = server.Address.AbsoluteUri; officeVm.UserName = User;
            ((CheckBox)officeWindow.FindName("SyncRegisterBox")).IsChecked = true;
            ((PasswordBox)officeWindow.FindName("SyncPasswordBox")).Password = Password;
            ((PasswordBox)officeWindow.FindName("SyncConfirmationBox")).Password = "PUBLIC mismatch password";
            ((PasswordBox)officeWindow.FindName("SyncInviteBox")).Password = Invite;
            Click(officeWindow, "SyncLoginButton"); Wait(() => !officeVm.Busy);
            Require(!office.State.SignedIn && officeVm.Status.Contains("不一致"), "Registration ignored mismatched password confirmation.");
            ((PasswordBox)officeWindow.FindName("SyncConfirmationBox")).Password = Password;
            Preview(officeWindow, "47-account-sync-login");
            stage = "register from actual controls";
            Click(officeWindow, "SyncLoginButton"); Wait(() => office.State.SignedIn && !officeVm.Busy && office.State.Status.Contains("已加密同步"));
            Require(((PasswordBox)officeWindow.FindName("SyncPasswordBox")).Password.Length == 0 && ((PasswordBox)officeWindow.FindName("SyncInviteBox")).Password.Length == 0, "Login retained password/invite controls.");
            ((CheckBox)officeWindow.FindName("AutomaticSyncBox")).IsChecked = false; Wait(() => !office.State.Automatic && !officeVm.Busy);
            var homeTranslation = new OnlineTranslationSettingsStore(Path.Combine(home, "online-translation.json"));
            var homeVoice = new OnlineSpeechSettingsStore(homeTranslation, Path.Combine(home, "online-speech.json"));
            int refreshed = 0;
            house = new(home, app.Dispatcher, () => !app.Windows.OfType<OnlineSpeechSettingsWindow>().Any(), () => refreshed++, journal); Await(house.StartAsync());
            var homeVm = new AccountSyncViewModel(house, app.Dispatcher); var homeWindow = new AccountSyncWindow(homeVm); homeWindow.Show();
            homeVm.Server = server.Address.AbsoluteUri; homeVm.UserName = User;
            ((PasswordBox)homeWindow.FindName("SyncPasswordBox")).Password = Password;
            stage = "home login and Windows re-protection";
            Click(homeWindow, "SyncLoginButton"); Wait(() => house.State.SignedIn && !homeVm.Busy && refreshed > 0);
            ((CheckBox)homeWindow.FindName("AutomaticSyncBox")).IsChecked = false; Wait(() => !house.State.Automatic && !homeVm.Busy);
            Require(homeTranslation.ReadCredentials().ApiKey == "PUBLIC-WORK-TRANSLATION-KEY" && homeTranslation.Read()!.VisionModel == "PUBLIC-VISION" && homeTranslation.ReadProfiles().Count == 2, "Home translation/OCR configuration or empty-key profile was lost.");
            Require(homeVoice.ReadCredentials().ApiKey == "PUBLIC-WORK-VOICE-KEY" && homeVoice.ReadCredentials("PUBLIC-SHARED").ApiKey == "PUBLIC-WORK-TRANSLATION-KEY" && homeVoice.ReadMode() == SpeechMode.Online && homeVoice.Read()!.Speed == 1.25, "Home voice settings/shared key/default were not restored.");
            Require(new TranslationPreferencesStore(Path.Combine(home, "translation-preferences.json")).Read()!.Mode == TranslationMode.Online && new OcrPreferencesStore(Path.Combine(home, "ocr-preferences.json")).Read() == OcrMode.Online, "Defaults did not follow the account.");
            Require(File.ReadAllText(Path.Combine(work, "online-translation.json")) != File.ReadAllText(Path.Combine(home, "online-translation.json")), "Home copied a machine-bound encrypted file instead of re-protecting credentials.");
            var api = new AccountSyncApi(server.Address); var parameters = AwaitResult(api.ParametersAsync(User, default));
            var keys = AccountSyncProtocol.Derive(Password, parameters.Salt);
            var session = AwaitResult(api.LoginAsync(new() { UserName = User, AuthSecret = keys.AuthSecret }, default));
            var remote = AwaitResult(api.GetAsync(session.Token, null, default));
            byte[] clear = EncryptedConfigurationArchive.Decrypt(remote.Archive!, keys.VaultPassword);
            try { Require(!Encoding.UTF8.GetString(clear).Contains("PUBLIC-SHOULD-NOT-UPLOAD"), "Snapshot/history sentinel entered the sync archive."); }
            finally { CryptographicOperations.ZeroMemory(clear); }
            Console.WriteLine("PASS actual account login/registration controls, password clearing, home-device DPAPI re-protection, independent/shared voice keys, empty-key profiles, OCR/models/defaults and strict configuration-only scope.");

            stage = "ordinary upload and download";
            translation.Save(translation.Read()! with { Model = "PUBLIC-MODEL-NEW" }, "PUBLIC-NEW-KEY");
            Click(officeWindow, "SyncNowButton"); Wait(() => !officeVm.Busy);
            Click(homeWindow, "SyncNowButton"); Wait(() => !homeVm.Busy);
            Require(homeTranslation.Read()!.Model == "PUBLIC-MODEL-NEW" && homeTranslation.ReadCredentials().ApiKey == "PUBLIC-NEW-KEY", "Ordinary updates did not reach the home device.");
            stage = "conflict preserves local until explicit choice";
            translation.Save(translation.Read()! with { Model = "PUBLIC-WORK-CONFLICT" }, null);
            homeTranslation.Save(homeTranslation.Read()! with { Model = "PUBLIC-HOME-CONFLICT" }, null);
            Click(officeWindow, "SyncNowButton"); Wait(() => !officeVm.Busy);
            Click(homeWindow, "SyncNowButton"); Wait(() => !homeVm.Busy && house.State.Conflict);
            Require(homeTranslation.Read()!.Model == "PUBLIC-HOME-CONFLICT" && homeVm.UseCloudCommand.CanExecute(null) && homeVm.UseLocalCommand.CanExecute(null), "Conflict silently replaced local settings or has no resolution.");
            Preview(homeWindow, "48-account-sync-conflict");
            Click(homeWindow, "UseLocalButton"); Wait(() => !homeVm.Busy && !house.State.Conflict);
            Click(officeWindow, "SyncNowButton"); Wait(() => !officeVm.Busy);
            Require(translation.Read()!.Model == "PUBLIC-HOME-CONFLICT", "Explicit local choice did not become the remote version.");
            Console.WriteLine("PASS real two-device updates, conflict pause without overwriting, and explicit keep-local resolution.");

            stage = "unsaved settings guard";
            translation.Save(translation.Read()! with { Model = "PUBLIC-GUARDED-UPDATE" }, null);
            Click(officeWindow, "SyncNowButton"); Wait(() => !officeVm.Busy);
            using (var settingsVm = new OnlineSpeechSettingsViewModel(homeVoice, homeTranslation, _ => throw new InvalidOperationException("No provider request expected."), new NoAudio()))
            {
                var editing = new OnlineSpeechSettingsWindow(settingsVm); editing.Show(); settingsVm.Model = "PUBLIC-UNSAVED-DRAFT";
                Click(homeWindow, "SyncNowButton"); Wait(() => !homeVm.Busy);
                Require(homeTranslation.Read()!.Model == "PUBLIC-HOME-CONFLICT" && settingsVm.Model == "PUBLIC-UNSAVED-DRAFT" && house.State.Status.Contains("关闭"), "Sync replaced configuration underneath an open settings editor.");
                editing.Close();
            }
            Click(homeWindow, "SyncNowButton"); Wait(() => !homeVm.Busy);
            Require(homeTranslation.Read()!.Model == "PUBLIC-GUARDED-UPDATE", "Closing settings did not allow the pending update.");
            Console.WriteLine("PASS open settings keep unsaved edits and defer remote application until the editor closes.");

            stage = "transaction failure and crash recovery";
            var transfer = new SettingsConfigurationTransfer(home); var before = transfer.Capture();
            translation.Save(translation.Read()! with { Model = "PUBLIC-ROLLBACK-UPDATE" }, null);
            byte[] bundle = new SettingsConfigurationTransfer(work).Encrypt(new SettingsConfigurationTransfer(work).Capture(), keys.VaultPassword);
            var prepared = transfer.Decrypt(bundle, keys.VaultPassword);
            using (var locked = new FileStream(Path.Combine(home, "online-speech.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { transfer.Apply(prepared, before.Fingerprint); throw new InvalidOperationException("Locked settings replacement was accepted."); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            transfer.RecoverInterruptedImport();
            Require(transfer.Capture().Fingerprint == before.Fingerprint && homeTranslation.ReadCredentials().ApiKey == "PUBLIC-NEW-KEY", "Transaction recovery lost the previous credentials.");
            homeTranslation.Save(homeTranslation.Read()! with { Model = "PUBLIC-NEW-LOCAL" }, null);
            try { transfer.Apply(prepared, before.Fingerprint); throw new InvalidOperationException("Stale local fingerprint was accepted."); }
            catch (InvalidOperationException error) when (error.Message.Contains("新修改")) { }
            Require(homeTranslation.Read()!.Model == "PUBLIC-NEW-LOCAL", "Stale prepared import erased a new edit.");
            Console.WriteLine("PASS actual Windows file-lock failure, encrypted rollback journal recovery and stale-local-edit protection.");

            // Restore the accepted local baseline via an explicit cloud choice, then queue a new
            // download while this test intentionally holds the dispatcher. Logout must cancel it.
            Click(officeWindow, "SyncNowButton"); Wait(() => !officeVm.Busy);
            Click(homeWindow, "SyncNowButton"); Wait(() => !homeVm.Busy && house.State.Conflict);
            Click(homeWindow, "UseCloudButton"); Wait(() => !homeVm.Busy && !house.State.Conflict);
            string preserved = homeTranslation.Read()!.Model;
            translation.Save(translation.Read()! with { Model = "PUBLIC-LATE-UPDATE" }, null);
            Click(officeWindow, "SyncNowButton"); Wait(() => !officeVm.Busy);
            using var validated = new ManualResetEventSlim();
            house.Changed += (_, _) => { if (house.State.Status.StartsWith("云端配置已验证", StringComparison.Ordinal)) validated.Set(); };
            var downloading = house.SynchronizeAsync();
            Require(validated.Wait(TimeSpan.FromSeconds(8)), "Public download did not reach the dispatcher boundary.");
            var loggingOut = house.LogoutAsync(); Await(Task.WhenAll(downloading, loggingOut));
            Require(!house.State.SignedIn && homeTranslation.Read()!.Model == preserved && !File.Exists(Path.Combine(home, "account-sync.json")), "Late download changed credentials after logout or retained the session.");
            Console.WriteLine("PASS logout cancels a validated queued download and preserves local credentials without a late import.");
            string protectedState = File.ReadAllText(Path.Combine(work, "account-sync.json"));
            Require(!protectedState.Contains(Password) && !protectedState.Contains(keys.VaultPassword) && !protectedState.Contains("PUBLIC-NEW-KEY"), "Account settings persist plaintext secrets.");
            officeWindow.Close(); homeWindow.Close(); office.Dispose(); office = null;
            restarted = new(work, app.Dispatcher, () => true, () => { }, journal); Await(restarted.StartAsync());
            Require(restarted.State.SignedIn && !restarted.State.Automatic, "Restart lost the protected session or automatic-sync preference.");
            Await(restarted.SynchronizeAsync());
            journal.FlushAsync().GetAwaiter().GetResult();
            string log = File.ReadAllText(Path.Combine(folder, "diagnostics", "events.jsonl"));
            Require(!log.Contains(Password) && !log.Contains("PUBLIC-NEW-KEY") && !log.Contains(User) && !log.Contains("public.example.com") && !log.Contains(keys.VaultPassword), "Sync diagnostics disclosed credentials, usernames or service endpoints.");
            Console.WriteLine("PASS protected restart/session/default, independent logout and diagnostics privacy; no actual Aliyun server or paid provider was contacted.");
            return 0;
        }
        catch (Exception error)
        {
            Console.WriteLine("::error title=Account sync UI::" + stage + ": " + error.GetType().Name + ": " + error.Message.Replace("%", "%25").Replace("\n", "%0A").Replace("\r", "%0D"));
            return 1;
        }
        finally
        {
            office?.Dispose(); house?.Dispose(); restarted?.Dispose();
            foreach (Window window in app.Windows.Cast<Window>().ToArray()) window.Close();
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }
    private static void Wait(Func<bool> predicate) => TextWindowChecks.PumpUntil(predicate, TimeSpan.FromSeconds(15));
    private static void Await(Task task) { Wait(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static T AwaitResult<T>(Task<T> task) { Await(task); return task.GetAwaiter().GetResult(); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Click(Window window, string name)
    {
        var button = (Button)window.FindName(name);
        if (button.Command is { } command) { Require(command.CanExecute(null), "Disabled command: " + name); command.Execute(null); }
        else button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    private static void Preview(Window window, string name)
    {
        window.UpdateLayout(); var a = window.PointToScreen(new Point()); var b = window.PointToScreen(new Point(window.ActualWidth, window.ActualHeight));
        var display = new DesktopCaptureService(); var bounds = PixelRect.FromPoints(new(a.X, a.Y), new(b.X, b.Y));
        var screen = display.GetDisplays().First(d => d.Primary).Bounds; bounds = bounds.Intersect(screen);
        Directory.CreateDirectory("artifacts/validation/ui-previews");
        new ImageExportService().SavePng(display.CaptureRegion(bounds), "artifacts/validation/ui-previews/" + name + ".png");
    }
    private sealed class NoAudio : ISpeechAudioPlayer { public Task PlayAsync(SpeechAudio audio, double speed, CancellationToken token) => throw new InvalidOperationException("No provider request expected."); }
    private sealed class Server : IDisposable
    {
        private readonly string folder; private Process? process;
        public Uri Address { get; }
        public Server(string folder)
        {
            this.folder = folder; Directory.CreateDirectory(folder);
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); Address = new("http://127.0.0.1:" + port + "/");
        }
        public async Task StartAsync()
        {
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = folder };
            start.ArgumentList.Add(Path.GetFullPath("src/Snip.Sync.Server/bin/Release/net8.0/Snip.Sync.Server.dll"));
            start.Environment["ASPNETCORE_URLS"] = Address.AbsoluteUri.TrimEnd('/'); start.Environment["SELF_SNIP_DATA"] = Path.Combine(folder, "data"); start.Environment["SELF_SNIP_REGISTER_INVITE"] = Invite;
            process = Process.Start(start)!; _ = process.StandardOutput.ReadToEndAsync(); _ = process.StandardError.ReadToEndAsync();
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
            for (int i = 0; i < 100; i++)
            {
                if (process.HasExited) throw new InvalidOperationException("Public server exited before ready.");
                try { if ((await client.GetAsync(new Uri(Address, "health"))).IsSuccessStatusCode) return; } catch (HttpRequestException) { }
                await Task.Delay(100);
            }
            throw new TimeoutException("Public server readiness timed out.");
        }
        public void Dispose() { if (process is { HasExited: false }) { process.Kill(true); process.WaitForExit(5000); } process?.Dispose(); }
    }
}
