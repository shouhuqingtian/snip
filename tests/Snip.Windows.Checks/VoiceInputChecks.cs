using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Snip.App.Services;
using Snip.App.ViewModels;
using Snip.App.Views;
using Snip.Core;
using Snip.Infrastructure;

namespace Snip.Windows.Checks;

internal static class VoiceInputChecks
{
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern bool SetPhysicalCursorPos(int x, int y);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public Union Data; }
    [StructLayout(LayoutKind.Explicit)] private struct Union { [FieldOffset(0)] public KeyData Key; [FieldOffset(0)] public MouseData Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyData { public ushort Key, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseData { public int X, Y; public uint Wheel, Flags, Time; public UIntPtr Extra; }
    private const string Key = "PUBLIC-ASR-KEY";
    private static readonly HotkeyGesture Gesture = new(3, 0x20);
    public static int RecordFixture() => VoiceRecordingWorker.RunAsync(Console.In, Console.OpenStandardOutput(), new FakeRecorder()).GetAwaiter().GetResult();
    public static int JobParent(string path)
    {
        var recorder = new IsolatedMicrophoneRecorder(RecordHost); recorder.WorkerStarted += id => File.WriteAllText(path, id.ToString());
        recorder.RecordAsync(CancellationToken.None, CancellationToken.None, () => Environment.Exit(0)).GetAwaiter().GetResult(); return 1;
    }
    private static ProcessStartInfo RecordHost()
    {
        var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Snip.Windows.Checks.exe")); info.ArgumentList.Add("--voice-record-fixture"); return info;
    }
    public static int Run()
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var previous = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        string folder = Path.Combine(Path.GetTempPath(), "Snip.Voice.Check-" + Guid.NewGuid()); Directory.CreateDirectory(folder);
        var owner = new Window { Width = 640, Height = 400, Left = 30, Top = 50, Title = "Public voice input fixture" };
        VoiceInputController? controller = null; VoiceInputStatusWindow? status = null;
        using var watchdog = new System.Threading.Timer(_ => { Console.Error.WriteLine("FAIL voice input deadline."); Environment.Exit(1); }, null, TimeSpan.FromMinutes(3), Timeout.InfiniteTimeSpan);
        try
        {
            Ui.EnsureTheme();
            var input = new TextBox { Name = "FirstVoiceInput", Text = "prefixsuffix", Margin = new Thickness(12), FontSize = 18 };
            var other = new TextBox { Text = "untouched", Margin = new Thickness(12), FontSize = 18 };
            var password = new PasswordBox { Margin = new Thickness(12) };
            var panel = new StackPanel(); panel.Children.Add(input); panel.Children.Add(other); panel.Children.Add(password); owner.Content = panel; owner.Show(); owner.Activate(); owner.UpdateLayout();
            var translations = new OnlineTranslationSettingsStore(Path.Combine(folder, "translation.json"));
            translations.Save(new("PUBLIC-SHARED", "https://shared.example.com/v1", "PUBLIC-TEXT-MODEL"), "PUBLIC-SHARED-KEY");
            var store = new TranscriptionSettingsStore(translations, Path.Combine(folder, "voice-input.json"));
            var recorder = new FakeRecorder(); var recognizer = new FakeTranscriber();
            using var journal = new DiagnosticJournal(Path.Combine(folder, "logs"));
            controller = new(store, app.Dispatcher, journal, recorder, new VoiceTargetProbe(AppHost), _ => recognizer);
            controller.Initialize(); Require(!controller.Enabled && recorder.Calls == 0, "Voice input opened microphone before explicit setup.");
            var vm = new VoiceInputSettingsViewModel(store, translations, controller); var settings = new VoiceInputSettingsWindow(vm); settings.Show(); settings.UpdateLayout();
            vm.Endpoint = "https://asr.example.com/v1/audio/transcriptions"; vm.Model = "PUBLIC-ASR"; vm.SetGesture(Gesture);
            ((PasswordBox)settings.FindName("VoiceKeyBox")).Password = Key;
            Click((Button)settings.FindName("SaveVoiceInput"));
            Require(controller.Enabled && store.ReadPreferences().Hotkey == Gesture && store.ReadCredentials().ApiKey == Key && ((PasswordBox)settings.FindName("VoiceKeyBox")).Password.Length == 0, "ASR settings did not register/save or clear key display: " + vm.Status);
            Require(!File.ReadAllText(Path.Combine(folder, "voice-input.json")).Contains(Key), "ASR key is plaintext on disk.");
            Preview(settings, "58-voice-input-settings");
            CheckSettingsHotkeys(settings, vm, controller, store, recorder);
            using (var occupant = new HotkeyService())
            {
                Require(occupant.Register(HotkeyGesture.Default), "Public screenshot conflict fixture could not register.");
                vm.SetGesture(HotkeyGesture.Default); vm.SaveCommand.Execute(null);
                Require(store.ReadPreferences().Hotkey == Gesture && vm.Status.Contains("占用"), "Shortcut conflict replaced existing voice settings.");
            }
            vm.SetGesture(Gesture); vm.KeyProfile = "PUBLIC-SHARED"; vm.Endpoint = "https://shared.example.com/v1/audio/transcriptions"; vm.SaveCommand.Execute(null);
            Require(store.ReadCredentials().ApiKey == "PUBLIC-SHARED-KEY", "Same-host ASR key reuse failed.");
            vm.Endpoint = "https://different.example.com/asr"; vm.SaveCommand.Execute(null);
            Require(store.Read()!.Endpoint == "https://shared.example.com/v1/audio/transcriptions" && vm.Status.Contains("域名"), "Cross-service key reuse was accepted or modified the saved profile.");
            settings.Close(); controller.Dispose();
            controller = new(store, app.Dispatcher, journal, recorder, new VoiceTargetProbe(AppHost), _ => recognizer); controller.Initialize();
            Require(controller.Enabled && store.ReadCredentials().ApiKey == "PUBLIC-SHARED-KEY", "Protected voice settings did not survive restart.");
            void ObserveVoice(object? sender, EventArgs args)
            {
                if (controller.SuppressNotification) { status?.Close(); return; }
                if (status is null) { status = new(controller, () => { }); var window = status; status.Closed += (_, _) => { if (ReferenceEquals(status, window)) status = null; }; status.Show(); }
                else status.UpdateStatus();
            }
            controller.Changed += ObserveVoice;
            Console.WriteLine("PASS actual ASR settings XAML, DPAPI keys, same-platform reuse, cross-host rejection, restart and native hotkey conflict preservation.");

            void Focus(TextBox box) { owner.Activate(); box.Focus(); box.SelectionStart = 6; box.SelectionLength = 0; Pause(); }
            void Start()
            {
                int count = recorder.Calls; recognizer.Reset(); Keys(false, 0x11, 0x12, 0x20);
                Wait(() => recorder.Calls == count + 1 && controller.State == VoiceInputState.Recording); Pause();
                Require(DesktopWindows.Foreground == new WindowInteropHelper(owner).Handle, "Voice HUD stole input focus.");
            }
            void Release()
            { Keys(true, 0x20, 0x12, 0x11); Wait(() => controller.State == VoiceInputState.Recognizing || !controller.IsActive); }
            Focus(input); Clipboard.SetText("PUBLIC-CLIPBOARD-SENTINEL"); Start();
            if (status is not null) Preview(status, "60-voice-input-recording");
            Require(recognizer.Calls == 0, "Voice was sent before shortcut release.");
            Release(); Wait(() => recognizer.Pending); recognizer.Result.SetResult("中文 English 🙂\n下一句");
            Wait(() => !controller.IsActive);
            Require(input.Text == "prefix中文 English 🙂 下一句suffix" && Clipboard.GetText() == "PUBLIC-CLIPBOARD-SENTINEL" && !controller.ReviewNeeded, "Native Unicode input differs, moved caret, touched clipboard or required manual paste.");
            Require(recorder.LastWave!.All(b => b == 0), "Completed recording bytes were retained.");
            Console.WriteLine("PASS native hold-to-record/release-to-recognize inserts mixed Chinese/English/emoji at the original WPF caret, keeps clipboard intact and clears audio bytes.");

            input.Text = "prefixsuffix"; Focus(input); Start(); Release(); Wait(() => recognizer.Pending);
            const string failureMessage = "语音识别失败（HTTP 400）：请求格式或参数不被接受，请核对 OpenAI 兼容接口。";
            recognizer.Result.SetException(new VoiceInputException(failureMessage, VoiceInputFailure.Service, 400)); Wait(() => !controller.IsActive);
            using (var failureVm = new VoiceInputSettingsViewModel(store, translations, controller))
                Require(failureVm.Status.Contains("HTTP 400") && controller.LastFailure == failureMessage && input.Text == "prefixsuffix", "Service failure was lost when reopening settings or typed into the input.");
            Console.WriteLine("PASS ASR service failure preserves its safe HTTP explanation when reopening settings and never inserts failure text into the original field.");

            input.Text = "prefixsuffix"; Focus(input); Start(); Release(); Wait(() => recognizer.Pending);
            Click(other); recognizer.Result.SetResult("PUBLIC-DO-NOT-INSERT"); Wait(() => !controller.IsActive);
            Require(controller.ReviewNeeded && controller.LastText == "PUBLIC-DO-NOT-INSERT" && input.Text == "prefixsuffix" && other.Text == "untouched" && Keyboard.FocusedElement == other, "Changed input field received dictation automatically or lost focus.");
            Preview(new VoiceInputResultWindow(controller.LastText), "59-voice-input-result", show: true);
            Console.WriteLine("PASS changing the input field during recognition preserves the result without injecting into either field or stealing focus.");

            Focus(input); Start(); int calls = recognizer.Calls; HotkeyChecks.Press(0x1B); Keys(true, 0x20, 0x12, 0x11); Wait(() => !controller.IsActive);
            Require(recognizer.Calls == calls && input.Text == "prefixsuffix", "Esc cancellation uploaded audio or typed text.");
            Focus(input); Start(); Release(); Wait(() => recognizer.Pending); controller.Cancel(); Wait(() => !controller.IsActive);
            Require(input.Text == "prefixsuffix", "Cancelled recognition typed a late result.");
            Console.WriteLine("PASS native Esc stops held recording before upload; recognition cancellation prevents late text input.");

            owner.Activate(); password.Focus(); Pause(); Start(); Release(); Wait(() => !controller.IsActive);
            Require(password.Password.Length == 0 && !recognizer.Pending, "Password field accepted dictation or uploaded voice.");
            other.IsReadOnly = true; Focus(other); Start(); Release(); Wait(() => recognizer.Pending); recognizer.Result.SetResult("PUBLIC-READONLY"); Wait(() => !controller.IsActive);
            Require(controller.ReviewNeeded && other.Text == "untouched", "Read-only field was changed.");
            Console.WriteLine("PASS UI Automation password/read-only detection prevents automatic entry; password recordings are not sent to ASR.");

            void SetMode(int index)
            {
                status?.Close();
                var modeVm = new VoiceInputSettingsViewModel(store, translations, controller);
                var modeWindow = new VoiceInputSettingsWindow(modeVm); modeWindow.Show(); modeWindow.Activate(); modeWindow.UpdateLayout();
                Click((ComboBox)modeWindow.FindName("VoiceModeBox")); HotkeyChecks.Press(index == 1 ? (ushort)0x23 : (ushort)0x24); HotkeyChecks.Press(0x0D);
                Wait(() => modeVm.ModeIndex == index); Click((Button)modeWindow.FindName("SaveVoiceInput"));
                Require(new TranscriptionSettingsStore(translations, Path.Combine(folder, "voice-input.json")).ReadPreferences().Mode == (VoiceInputMode)index && controller.Mode == (VoiceInputMode)index, "Actual mode selector did not save or activate the mode: " + modeVm.Status);
                if (index == 1) Preview(modeWindow, "58-voice-input-settings");
                modeWindow.Close(); status?.Close();
            }
            SetMode(1); controller.Dispose();
            controller = new(store, app.Dispatcher, journal, recorder, new VoiceTargetProbe(AppHost), _ => recognizer); controller.Initialize(); controller.Changed += ObserveVoice;
            using (var reopenedVm = new VoiceInputSettingsViewModel(store, translations, controller))
                Require(controller.Mode == VoiceInputMode.Toggle && reopenedVm.ModeIndex == 1, "Toggle recording mode did not survive restart.");
            Console.WriteLine("PASS actual native mode selector saves toggle/hold choices and restores the selected mode after controller/settings restart with credentials intact.");

            input.Text = "prefixsuffix"; other.IsReadOnly = false; Focus(input);
            int toggleRecordings = recorder.Calls, toggleRequests = recognizer.Calls;
            recognizer.Reset(); HotkeyChecks.Press(0x11, 0x12, 0x20);
            Wait(() => recorder.Calls == toggleRecordings + 1 && controller.State == VoiceInputState.Recording); Pause();
            Require(controller.State == VoiceInputState.Recording && recognizer.Calls == toggleRequests, "Releasing the shortcut stopped toggle recording or uploaded audio.");
            if (status is not null) Preview(status, "60-voice-input-recording");
            HotkeyChecks.Press(0x11, 0x12, 0x20); Wait(() => recognizer.Pending);
            HotkeyChecks.Press(0x11, 0x12, 0x20); Pause();
            Require(recorder.Calls == toggleRecordings + 1 && controller.State == VoiceInputState.Recognizing, "A shortcut during recognition restarted or interrupted the request.");
            recognizer.Result.SetResult("toggle 中文 words"); Wait(() => !controller.IsActive);
            Require(input.Text == "prefixtoggle 中文 wordssuffix" && !controller.ReviewNeeded && Clipboard.GetText() == "PUBLIC-CLIPBOARD-SENTINEL" && recorder.LastWave!.All(b => b == 0), "Toggle recording failed native caret insertion, clipboard preservation or audio cleanup.");
            Console.WriteLine("PASS native tap-to-start, release-keeps-recording, tap-again-to-stop and Unicode caret insertion; recognition ignores extra shortcuts without restarting.");

            input.Text = "prefixsuffix"; Focus(input); Start(); Keys(true, 0x20, 0x12, 0x11); Pause();
            int beforeToggleCancel = recognizer.Calls; HotkeyChecks.Press(0x1B); Wait(() => !controller.IsActive);
            Require(recognizer.Calls == beforeToggleCancel && input.Text == "prefixsuffix", "Esc cancellation in toggle mode uploaded audio or changed the input.");
            Focus(input); Start(); Keys(true, 0x20, 0x12, 0x11); Click(other);
            HotkeyChecks.Press(0x11, 0x12, 0x20); Wait(() => recognizer.Pending); recognizer.Result.SetResult("PUBLIC-TOGGLE-REVIEW"); Wait(() => !controller.IsActive);
            Require(controller.ReviewNeeded && input.Text == "prefixsuffix" && other.Text == "untouched" && Keyboard.FocusedElement == other, "Toggle stop injected into a changed input field or stole focus.");
            SetMode(0); Require(controller.Mode == VoiceInputMode.Hold, "Switching back to hold recording did not take effect.");
            Console.WriteLine("PASS toggle Esc cancellation before upload and changed-field result preservation; saved switch back to hold mode takes effect immediately.");

            status?.Close(); CheckChrome(controller, recorder, recognizer, folder); CheckRecorderWorker();
            controller.Dispose(); controller = null;
            journal.FlushAsync().GetAwaiter().GetResult(); string logs = File.ReadAllText(Path.Combine(folder, "logs/events.jsonl"));
            Require(!logs.Contains(Key) && !logs.Contains("PUBLIC-SHARED-KEY") && !logs.Contains("PUBLIC-DO-NOT-INSERT") && !logs.Contains("PUBLIC-TOGGLE-REVIEW") && !logs.Contains("asr.example.com"), "Voice content, endpoint or credentials entered diagnostics.");
            File.WriteAllText("artifacts/validation/voice-input.json", JsonSerializer.Serialize(new { NativeHotkeyHoldRelease = true, ProtectedSettings = true, CredentialReuseChecked = true, ConflictPreservesShortcut = true, NativeCaretInsertion = true, ClipboardUnchanged = true, FocusChangeNoInjection = true, CancellationNoUpload = true, PasswordNoUpload = true, ReadOnlyPreserved = true, AudioCleared = true, ChromeTextareaInput = true, ChromeContentEditableInput = true, IsolatedRecorderPipe = true, WorkerReaped = true, ParentExitKillsWorker = true, DiagnosticsPrivate = true, TogglePressTwice = true, ToggleReleaseKeepsRecording = true, RecordingModePersists = true, ToggleCancelNoUpload = true, ToggleFocusChangeNoInjection = true, SettingsScreenshotAvailable = true, ShortcutCaptureRequiresClick = true, ShortcutCaptureEndsAfterOne = true, ShortcutCaptureCancelsOnBlur = true, ShortcutDraftNotApplied = true, ServiceFailureVisibleInSettings = true }));
            return 0;
        }
        catch (Exception error)
        {
            string log = Path.Combine(folder, "logs/events.jsonl");
            if (File.Exists(log)) foreach (string line in File.ReadLines(log).TakeLast(8)) Console.Error.WriteLine(line);
            Console.Error.WriteLine(error); return 1;
        }
        finally { Keys(true, 0x20, 0x12, 0x11); controller?.Dispose(); status?.Close(); foreach (var w in app.Windows.Cast<Window>().ToArray()) w.Close(); Directory.Delete(folder, true); SynchronizationContext.SetSynchronizationContext(previous); }
    }
    private static void CheckSettingsHotkeys(VoiceInputSettingsWindow settings, VoiceInputSettingsViewModel vm, VoiceInputController controller, TranscriptionSettingsStore store, FakeRecorder recorder)
    {
        var desktop = new DesktopCaptureService(); var renderer = new AnnotationRenderer(); var export = new ImageExportService();
        using var pins = new PinManager(export);
        using var coordinator = new CaptureCoordinator(desktop, renderer, export, new FeatureHub(renderer, export, desktop, pins));
        using var screenshot = new HotkeyService();
        Require(screenshot.Register(HotkeyGesture.Default), "Settings screenshot fixture could not register.");
        Task? capture = null; int captures = 0, recordings = recorder.Calls;
        void OnCapture(object? sender, EventArgs args)
        { if (settings.TryCaptureRegisteredGesture(HotkeyGesture.Default)) return; captures++; capture = coordinator.CaptureAsync(); }
        void OnVoice(HotkeyGesture gesture) => settings.TryCaptureRegisteredGesture(gesture);
        var canStart = controller.CanStart; controller.CanStart = () => !settings.IsActive;
        screenshot.Triggered += OnCapture; controller.ShortcutWhileConfiguring += OnVoice;
        var box = (TextBox)settings.FindName("VoiceGestureBox"); var endpoint = (TextBox)settings.FindName("VoiceEndpointBox");
        try
        {
            settings.Activate(); endpoint.Focus(); Pause();
            Require(!settings.IsCapturingGesture, "Opening voice settings armed shortcut capture.");
            HotkeyChecks.Press(0x12, 0x57); HotkeyChecks.Press(0x11, 0x12, 0x20); Pause();
            Require(vm.GestureText == Gesture.DisplayText && recorder.Calls == recordings, "Ordinary settings keys changed the shortcut or started recording.");
            HotkeyChecks.Press(0x11, 0x12, 0x41);
            Wait(() => Application.Current.Windows.OfType<SelectionWindow>().Any(w => w.IsActive));
            HotkeyChecks.Press(0x0D); Wait(() => capture?.IsCompleted == true); capture!.GetAwaiter().GetResult();
            Require(captures == 1 && Clipboard.ContainsImage() && vm.GestureText == Gesture.DisplayText && settings.IsVisible && !settings.IsCapturingGesture, "Screenshot shortcut was swallowed, changed voice shortcut or failed to copy the settings window.");
            Console.WriteLine("PASS native screenshot shortcut opens the real selection coordinator and Enter copies while voice settings are open; ordinary keys and the voice shortcut do not change the draft.");

            settings.Activate(); Click((Button)settings.FindName("ChangeVoiceGesture")); Require(settings.IsCapturingGesture, "Explicit shortcut edit button did not arm capture.");
            HotkeyChecks.Press(0x11, 0x10, 0x77); Wait(() => vm.GestureText == "Ctrl+Shift+F8");
            Require(!settings.IsCapturingGesture && store.ReadPreferences().Hotkey == Gesture && controller.Shortcut == Gesture.DisplayText, "One-shot shortcut edit applied an unsaved draft or stayed armed.");
            HotkeyChecks.Press(0x12, 0x57); Pause(); Require(vm.GestureText == "Ctrl+Shift+F8", "Later keys replaced a completed shortcut draft.");
            Click(box); Require(settings.IsCapturingGesture, "Clicking the shortcut box did not arm capture.");
            HotkeyChecks.Press(0x11, 0x12, 0x41); Wait(() => vm.GestureText == HotkeyGesture.Default.DisplayText);
            Require(!settings.IsCapturingGesture && captures == 1, "Registered screenshot shortcut was not captured exactly once while explicitly editing.");
            Click(box); HotkeyChecks.Press(0x1B); Pause(); Require(!settings.IsCapturingGesture && settings.IsVisible, "Esc did not cancel shortcut editing without closing settings.");
            Click(box); Click(endpoint); HotkeyChecks.Press(0x12, 0x57); Pause();
            Require(!settings.IsCapturingGesture && vm.GestureText == HotkeyGesture.Default.DisplayText && store.ReadPreferences().Hotkey == Gesture, "Clicking another field did not cancel editing or changed saved settings.");
            Console.WriteLine("PASS explicit native click is required for one-shot shortcut editing; registered keys are captured only while editing; Esc/blur cancel and unsaved drafts leave the working registration intact.");
        }
        finally
        {
            screenshot.Triggered -= OnCapture; controller.ShortcutWhileConfiguring -= OnVoice; controller.CanStart = canStart;
            vm.SetGesture(Gesture);
        }
    }
    private static ProcessStartInfo AppHost() => new(Path.Combine(AppContext.BaseDirectory, "Snip.exe"));
    private static void CheckChrome(VoiceInputController controller, FakeRecorder recorder, FakeTranscriber recognizer, string folder)
    {
        string chrome = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }
            .Select(p => Path.Combine(p, "Google/Chrome/Application/chrome.exe")).First(File.Exists);
        var info = new ProcessStartInfo(chrome) { UseShellExecute = false };
        foreach (string arg in new[] { "--new-window", "--no-first-run", "--disable-background-mode", "--disable-gpu", "--user-data-dir=" + Path.Combine(folder, "chrome"), "--window-size=800,650", new Uri(Path.Combine(AppContext.BaseDirectory, "Fixtures/voice-input.html")).AbsoluteUri }) info.ArgumentList.Add(arg);
        using var launcher = Process.Start(info)!; WindowCaptureInfo? window = null;
        string Title() { var s = new StringBuilder(512); GetWindowText(window!.Handle, s, s.Capacity); return s.ToString().Split(" - ", 2)[0]; }
        try
        {
            Wait(() => (window = DesktopWindows.Snapshot().FirstOrDefault(w =>
            { var s = new StringBuilder(512); GetWindowText(w.Handle, s, s.Capacity); return s.ToString().StartsWith("Public voice fixture |", StringComparison.Ordinal); })) is not null);
            DesktopWindows.Activate(window!.Handle); Pause();
            foreach (var test in new[] { ("ONEOK", "PUBLIC 中文 voice"), ("TWOOK", "PUBLIC English words") })
            {
                if (test.Item1 == "TWOOK") { HotkeyChecks.Press(0x09); Wait(() => Title().EndsWith("| TWO", StringComparison.Ordinal)); }
                int before = recorder.Calls; recognizer.Reset(); Keys(false, 0x11, 0x12, 0x20); Wait(() => recorder.Calls == before + 1); Pause();
                Keys(true, 0x20, 0x12, 0x11); Wait(() => recognizer.Pending); recognizer.Result.SetResult(test.Item2); Wait(() => !controller.IsActive);
                Require(!controller.ReviewNeeded, "Chrome input could not be verified: " + controller.Status);
                Wait(() => Title().EndsWith("| " + test.Item1, StringComparison.Ordinal));
            }
            Console.WriteLine("PASS real Chrome textarea and contenteditable accept native ASR Unicode input at the original focus, with no Return key or submit action.");
        }
        finally
        {
            if (window is not null) { try { using var browser = Process.GetProcessById(window.ProcessId); if (!browser.HasExited) { browser.Kill(true); browser.WaitForExit(1000); } } catch (ArgumentException) { } }
            else if (!launcher.HasExited) { launcher.Kill(true); launcher.WaitForExit(1000); }
        }
    }
    private static void CheckRecorderWorker()
    {
        var recorder = new IsolatedMicrophoneRecorder(RecordHost); int pid = 0; recorder.WorkerStarted += id => pid = id;
        using var stop = new CancellationTokenSource(); bool ready = false;
        var recording = recorder.RecordAsync(stop.Token, default, () => ready = true); Wait(() => ready); stop.Cancel(); Wait(() => recording.IsCompleted);
        using (var audio = recording.GetAwaiter().GetResult()) Require(audio.Wav.Length > 44, "Synthetic worker audio pipe returned empty data.");
        Wait(() => Exited(pid));
        string marker = Path.Combine(Path.GetTempPath(), "Snip.Voice.Job-" + Guid.NewGuid() + ".txt");
        try
        {
            var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Snip.Windows.Checks.exe")) { UseShellExecute = false, CreateNoWindow = true }; info.ArgumentList.Add("--voice-job-parent"); info.ArgumentList.Add(marker);
            using var parent = Process.Start(info)!; Wait(() => File.Exists(marker) && parent.HasExited);
            Require(parent.ExitCode == 0, "Recorder parent fixture did not exit at ready.");
            int child = int.Parse(File.ReadAllText(marker)); Wait(() => Exited(child));
        }
        finally { File.Delete(marker); }
        Console.WriteLine("PASS actual bounded recording helper transports synthetic WAV only over pipes, exits after stop and is killed by the Windows job when its parent terminates.");
    }
    private static bool Exited(int id) { try { using var p = Process.GetProcessById(id); return p.HasExited; } catch (ArgumentException) { return true; } }
    private static void Keys(bool up, params ushort[] keys)
    { var inputs = keys.Select(k => new Input { Type = 1, Data = new() { Key = new() { Key = k, Flags = up ? 2u : 0u } } }).ToArray(); Require(SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) == inputs.Length, "Native key input failed."); }
    private static void Click(FrameworkElement element)
    {
        var point = element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2)); SetPhysicalCursorPos((int)point.X, (int)point.Y);
        foreach (uint flags in new[] { 2u, 4u }) { Input[] input = [new() { Data = new() { Mouse = new() { Flags = flags } } }]; Require(SendInput(1, input, Marshal.SizeOf<Input>()) == 1, "Native click failed."); } Pause();
    }
    private static void Wait(Func<bool> done) => TextWindowChecks.PumpUntil(done, TimeSpan.FromSeconds(10));
    private static void Pause() { var delay = Task.Delay(150); Wait(() => delay.IsCompleted); }
    private static void Preview(Window window, string name, bool show = false)
    {
        if (show) window.Show(); window.UpdateLayout(); var content = (FrameworkElement)window.Content;
        var margin = content.Margin; double width = content.ActualWidth + margin.Left + margin.Right, height = content.ActualHeight + margin.Top + margin.Bottom;
        var drawing = new System.Windows.Media.DrawingVisual();
        using (var dc = drawing.RenderOpen())
        {
            dc.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
            var offset = System.Windows.Media.VisualTreeHelper.GetOffset(content);
            var brush = new System.Windows.Media.VisualBrush(content) { ViewboxUnits = System.Windows.Media.BrushMappingMode.Absolute, Viewbox = new Rect(offset.X, offset.Y, content.ActualWidth, content.ActualHeight), Stretch = System.Windows.Media.Stretch.Fill };
            dc.DrawRectangle(brush, null, new Rect(margin.Left, margin.Top, content.ActualWidth, content.ActualHeight));
        }
        var image = new RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, System.Windows.Media.PixelFormats.Pbgra32); image.Render(drawing);
        Directory.CreateDirectory("artifacts/validation/ui-previews"); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        string path = "artifacts/validation/ui-previews/" + name + ".png";
        using (var file = File.Create(path)) encoder.Save(file);
        if (show) window.Close();
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class FakeRecorder : IMicrophoneRecorder
    {
        public int Calls; public byte[]? LastWave;
        public async Task<RecordedVoice> RecordAsync(CancellationToken stop, CancellationToken cancel, Action started)
        {
            Interlocked.Increment(ref Calls); started(); using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop, cancel);
            try { await Task.Delay(Timeout.Infinite, linked.Token); } catch (OperationCanceledException) { cancel.ThrowIfCancellationRequested(); }
            byte[] pcm = new byte[16000]; for (int i = 0; i < pcm.Length; i += 2) System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i), (short)(i % 3000 - 1500));
            LastWave = VoiceText.PcmToWav(pcm); return new(LastWave);
        }
    }
    private sealed class FakeTranscriber : IAudioTranscriber
    {
        public int Calls; public bool Pending; public TaskCompletionSource<string> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Reset() { Pending = false; Result = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        public async Task<string> TranscribeAsync(RecordedVoice audio, CancellationToken token) { Calls++; Pending = true; return await Result.Task.WaitAsync(token); }
    }
}
