using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Snip.App.ViewModels;
using Snip.App.Views;
using Snip.Core;
using Snip.Infrastructure;
using Snip.Translation;

namespace Snip.Windows.Checks;

internal static class ClipboardWindowChecks
{
    private const string Fixture = "Clipboard regression: 中文 English 😀\r\nSecond line";
    private const string Sentinel = "Existing clipboard fixture";

    public static int Run()
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        string folder = Path.Combine(Path.GetTempPath(), "Snip.Clipboard-" + Guid.NewGuid());
        using var diagnostics = new DiagnosticJournal(folder);
        string stage = "initialization";
        var elapsed = Stopwatch.StartNew(); long stageStart = 0;
        using var watchdog = new System.Threading.Timer(_ =>
        {
            Console.WriteLine("::error title=Clipboard check deadline::Timed out in " + Volatile.Read(ref stage) + $"; total={elapsed.ElapsedMilliseconds}ms; stage={elapsed.ElapsedMilliseconds-Interlocked.Read(ref stageStart)}ms");
            CaptureStack();
            Environment.Exit(1);
        }, null, TimeSpan.FromSeconds(45), Timeout.InfiniteTimeSpan);
        void Stage(string value) { stage = value; Interlocked.Exchange(ref stageStart, elapsed.ElapsedMilliseconds); Console.WriteLine("::notice title=Clipboard check stage::" + value + $"; elapsed={elapsed.ElapsedMilliseconds}ms"); }
        Exception? dispatcherError = null;
        DispatcherUnhandledExceptionEventHandler handler = (_, e) => { dispatcherError = e.Exception; e.Handled = true; };
        app.DispatcherUnhandledException += handler;
        try
        {
            var image = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8); image.Freeze();
            TextResultWindow Show()
            {
                var vm = new TextResultViewModel(image, new TextWindowChecks.FakeOcr(), new ModelStore("unused-models"), new TextWindowChecks.FakeTranslation(), diagnostics) { AutoTranslate = true };
                var window = new TextResultWindow(vm); window.Show();
                TextWindowChecks.PumpUntil(() => !vm.Busy && vm.Translated.Length > 0, TimeSpan.FromSeconds(5));
                return window;
            }
            void Click(TextResultWindow window, string name)
            {
                var button = (Button)window.FindName(name);
                if (!button.IsEnabled) return;
                ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            }
            void WaitCopy(TextResultViewModel vm) => TextWindowChecks.PumpUntil(() => vm.CopyStatus.Contains("已复制") || vm.CopyStatus.Contains("复制未完成") || vm.CopyStatus.Contains("复制失败"), TimeSpan.FromSeconds(8));

            Stage("original failure reproduction");
            using (var holder = new ClipboardLock())
            {
                bool reproduced = false;
                try { Clipboard.SetText(Fixture); }
                catch (ExternalException error) when (error.HResult == TextClipboardService.ClipboardBusy) { reproduced = true; }
                Require(reproduced, "The original clipboard failure did not reproduce under a real lock.");
            }
            Console.WriteLine("PASS original Clipboard.SetText reproduces the reported 0x800401D0 under a real external lock.");

            Stage("Unicode source copy in actual window");
            var window = Show(); var vm = (TextResultViewModel)window.DataContext;
            ((TextBox)window.FindName("RecognizedText")).Text = Fixture;
            Click(window, "CopySourceButton"); WaitCopy(vm);
            Require(Clipboard.GetText() == Fixture && vm.Source == Fixture && window.IsVisible, "Edited Unicode source was not copied intact.");
            Require(((TextBlock)window.FindName("ClipboardStatus")).Text == vm.CopyStatus, "Copy feedback is not visible.");
            window.Close(); Require(Clipboard.GetText() == Fixture, "Closing the result discarded clipboard data.");
            Console.WriteLine("PASS real copy button copies edited Unicode and multiline text, shows feedback and persists after window close.");

            Stage("translation and empty source copy");
            window = Show(); vm = (TextResultViewModel)window.DataContext;
            Click(window, "CopyTranslationButton"); WaitCopy(vm);
            Require(Clipboard.GetText() == "你好，世界", "Translation button copied the wrong text.");
            vm.Source = "";
            Require(!vm.CopySourceCommand.CanExecute(null), "Empty source copy was enabled.");
            Click(window, "CopySourceButton"); Require(Clipboard.GetText() == "你好，世界", "Empty copy overwrote the clipboard.");
            window.Close(); Console.WriteLine("PASS translation copy and empty source preserve the correct clipboard data.");

            Stage("transient external lock and dispatcher responsiveness");
            window = Show(); vm = (TextResultViewModel)window.DataContext; vm.Source = Fixture;
            using (var holder = new ClipboardLock())
            {
                int ticks = 0;
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
                timer.Tick += (_, _) => { if (++ticks == 8) holder.Release(); };
                timer.Start();
                try
                {
                    Click(window, "CopySourceButton");
                    Require(!vm.CopySourceCommand.CanExecute(null) && !vm.CopyTranslationCommand.CanExecute(null), "Concurrent copy commands were enabled.");
                    vm.CopyTranslationCommand.Execute(null); vm.CopySourceCommand.Execute(null);
                    WaitCopy(vm);
                    Require(ticks >= 8 && vm.CopyStatus.Contains("已复制") && Clipboard.GetText() == Fixture, "Transient lock did not recover or dispatcher stalled.");
                }
                finally { timer.Stop(); }
            }
            window.Close(); Console.WriteLine("PASS another process holds the clipboard: UI stays responsive, duplicate clicks are ignored and release recovers automatically.");

            Stage("persistent external lock and later retry");
            window = Show(); vm = (TextResultViewModel)window.DataContext;
            Stage("persistent lock: writing sentinel");
            Clipboard.SetText(Sentinel);
            Stage("persistent lock: acquiring external holder");
            using (var holder = new ClipboardLock())
            {
                var clock = Stopwatch.StartNew(); Stage("persistent lock: invoking real copy button"); Click(window, "CopySourceButton");
                Stage("persistent lock: awaiting bounded retry result"); WaitCopy(vm);
                Stage("persistent lock: verifying failure feedback");
                Require(clock.Elapsed < TimeSpan.FromSeconds(5) && vm.CopyStatus.Contains("复制未完成") && vm.Source == "Hello world" && window.IsVisible && vm.CopySourceCommand.CanExecute(null), "Persistent lock lost results, claimed success or exceeded the retry bound.");
            }
            Stage("persistent lock: reading preserved sentinel after release");
            Require(Clipboard.GetText() == Sentinel, "Failed copy cleared unrelated clipboard contents.");
            Stage("persistent lock: retrying after release");
            Click(window, "CopySourceButton"); WaitCopy(vm);
            Require(Clipboard.GetText() == "Hello world" && vm.CopyStatus.Contains("已复制"), "Retry after failure did not succeed.");
            window.Close(); Console.WriteLine("PASS persistent lock gives bounded failure, retains results and existing clipboard, and allows a later retry.");

            Stage("window close during retry");
            Stage("close retry: creating actual result window");
            window = Show(); vm = (TextResultViewModel)window.DataContext;
            Stage("close retry: writing sentinel"); Clipboard.SetText(Sentinel);
            Stage("close retry: acquiring external holder");
            using (var holder = new ClipboardLock())
            {
                Stage("close retry: invoking copy"); Click(window, "CopySourceButton");
                Stage("close retry: closing actual window"); window.Close();
                Stage("close retry: verifying cancelled commands");
                TextWindowChecks.PumpUntil(() => !vm.CopyStatus.Contains("已复制") && !vm.CopySourceCommand.CanExecute(null), TimeSpan.FromSeconds(2));
                // Drain the cancelled async command before allowing the holder to release the clipboard.
                var drain = Task.Delay(150); TextWindowChecks.PumpUntil(() => drain.IsCompleted, TimeSpan.FromSeconds(2));
                Stage("close retry: releasing holder");
            }
            Stage("close retry: reading preserved sentinel");
            Require(Clipboard.GetText() == Sentinel, "Closing during retry published a late clipboard result.");
            Console.WriteLine("PASS closing during a clipboard lock cancels retry without publishing a late result.");

            Stage("persistence after publisher process exit");
            using (var publisher = StartChild("--clipboard-persist"))
            {
                Require(publisher.WaitForExit(5000) && publisher.ExitCode == 0, "Clipboard publisher process failed.");
            }
            Require(Clipboard.GetText() == Fixture, "Clipboard text did not persist after its publishing process exited.");
            Console.WriteLine("PASS clipboard text remains available after the publishing process exits.");

            Stage("diagnostic privacy and lifecycle");
            diagnostics.FlushAsync().GetAwaiter().GetResult();
            string log = File.ReadAllText(Path.Combine(folder, "events.jsonl"));
            Require(log.Contains("text-clipboard") && log.Contains("clipboard-busy") && log.Contains("failed") && log.Contains("cancelled") && log.Contains("completed"), "Clipboard lifecycle was not logged.");
            Require(!log.Contains("Hello world") && !log.Contains("你好") && !log.Contains("Clipboard regression") && !log.Contains(Sentinel), "Clipboard text leaked to diagnostics.");
            Require(dispatcherError is null, "Unhandled dispatcher exception during copy.");
            Console.WriteLine("PASS clipboard success, retry, failure and cancellation logs contain no copied text.");
            return 0;
        }
        catch (Exception error)
        {
            Console.WriteLine("::error title=Clipboard result UI::" + error.ToString().Replace("%", "%25").Replace("\r", "%0D").Replace("\n", "%0A"));
            return 1;
        }
        finally
        {
            Stage("cleanup");
            foreach (Window window in app.Windows.Cast<Window>().ToArray()) window.Close();
            app.DispatcherUnhandledException -= handler;
            SynchronizationContext.SetSynchronizationContext(previous);
            diagnostics.Dispose(); if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }
    private static void CaptureStack()
    {
        var executable = Environment.GetEnvironmentVariable("SNIP_CHECK_STACK_TOOL");
        if (string.IsNullOrEmpty(executable) || !File.Exists(executable)) return;
        try
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("report"); start.ArgumentList.Add("--process-id"); start.ArgumentList.Add(Environment.ProcessId.ToString());
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5000)) { process.Kill(true); return; }
            string stack = output.GetAwaiter().GetResult();
            // Thread stacks only: no dump, locals, memory, environment values, or clipboard text.
            Console.WriteLine("::error title=Clipboard managed thread stacks::" + stack[..Math.Min(stack.Length, 24000)].Replace("%", "%25").Replace("\r", "%0D").Replace("\n", "%0A"));
            _ = errors.GetAwaiter().GetResult();
        }
        catch { }
    }

    public static int PublishFixture()
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var copy = new TextClipboardService().CopyAsync(Fixture, CancellationToken.None);
        TextWindowChecks.PumpUntil(() => copy.IsCompleted, TimeSpan.FromSeconds(5));
        copy.GetAwaiter().GetResult();
        return 0;
    }

    public static int HoldClipboard(string acquiredName, string releaseName)
    {
        using var acquired = EventWaitHandle.OpenExisting(acquiredName);
        using var release = EventWaitHandle.OpenExisting(releaseName);
        if (!OpenClipboard(IntPtr.Zero)) return 1;
        try { acquired.Set(); return release.WaitOne(TimeSpan.FromSeconds(10)) ? 0 : 1; }
        finally { CloseClipboard(); }
    }

    private static Process StartChild(params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(typeof(ClipboardWindowChecks).Assembly.Location);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start)!;
    }

    private sealed class ClipboardLock : IDisposable
    {
        private readonly EventWaitHandle acquired, release;
        private readonly Process child;
        public ClipboardLock()
        {
            string id = Guid.NewGuid().ToString("N");
            acquired = new(false, EventResetMode.AutoReset, "Local\\SnipClipboardAcquired" + id);
            release = new(false, EventResetMode.ManualReset, "Local\\SnipClipboardRelease" + id);
            child = StartChild("--clipboard-lock", "Local\\SnipClipboardAcquired" + id, "Local\\SnipClipboardRelease" + id);
            if (!acquired.WaitOne(5000)) { Dispose(); throw new TimeoutException("Clipboard lock fixture did not acquire the clipboard."); }
        }
        public void Release() => release.Set();
        public void Dispose()
        {
            release.Set();
            if (!child.WaitForExit(2000)) child.Kill(true);
            child.Dispose(); acquired.Dispose(); release.Dispose();
        }
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseClipboard();
}
