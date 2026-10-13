using System.Threading;
using System.IO;
using System.Windows;
using Snip.App.Services;
using Snip.App.ViewModels;
using Snip.App.Views;
using Snip.Core;
using Snip.Infrastructure;

namespace Snip.App;

public partial class App : Application
{
    private Mutex? instance;
    private EventWaitHandle? activation;
    private RegisteredWaitHandle? activationListener;
    private IHotkeyService? hotkey;
    private ITrayService? tray;
    private CaptureCoordinator? coordinator;
    private PinManager? pins;
    private MainWindow? main;
    private bool ownsMutex;
    private DiagnosticJournal? diagnostics;
    private IDesktopCaptureService? desktop;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        diagnostics = new DiagnosticJournal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SelfSnip", "Logs"));
        diagnostics.Record("app", "started", new { Version = DesktopDiagnostics.Version });
        DispatcherUnhandledException += (_, args) => RecordUnhandled(args.Exception, "dispatcher-unhandled");
        AppDomain.CurrentDomain.UnhandledException += (_, args) => { if (args.ExceptionObject is Exception error) RecordUnhandled(error, "unhandled"); };
        TaskScheduler.UnobservedTaskException += (_, args) => diagnostics.Record("app", "unobserved-task", error: args.Exception);
        if (e.Args.Length == 2 && e.Args[0] == "--export-diagnostics")
        {
            try { var service = new DesktopCaptureService(); diagnostics.ExportAsync(e.Args[1], DesktopDiagnostics.EnvironmentInfo(service)).GetAwaiter().GetResult(); Shutdown(0); }
            catch (Exception error) { diagnostics.Record("diagnostics", "export-failed", error: error); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--wechat-diagnostics", StringComparer.Ordinal))
        {
            var diagnostics = new WeChatDiagnosticsWindow(); diagnostics.Closed += (_, _) => Shutdown(); diagnostics.Show(); return;
        }
        activation = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\SelfSnip.Activate.v1");
        instance = new Mutex(true, @"Local\SelfSnip.Instance.v1", out ownsMutex);
        if (!ownsMutex) { activation.Set(); Shutdown(); return; }
        try
        {
            // Explicit constructor injection keeps Windows concerns outside ViewModels.
            var capture = new DesktopCaptureService();
            desktop = capture;
            _ = Task.Run(() => diagnostics.Record("app", "environment", DesktopDiagnostics.EnvironmentInfo(capture)));
            var renderer = new AnnotationRenderer();
            var export = new DiagnosticImageExport(new ImageExportService(), diagnostics);
            pins = new PinManager(export);
            var features = new FeatureHub(renderer, export, capture, pins, diagnostics);
            coordinator = new CaptureCoordinator(capture, renderer, export, features, diagnostics);
            main = new MainWindow { DataContext = new MainViewModel(coordinator.CaptureAsync, ShowError, ExportDiagnosticsAsync) };
            MainWindow = main;
            hotkey = new HotkeyService(); tray = new TrayService();
            features.CaptureStatus = message => tray?.SetCaptureStatus(message);
            tray.CaptureCancelRequested += (_, _) => features.CancelBrowserCapture();
            hotkey.Triggered += (_, _) => StartCapture(); tray.CaptureRequested += (_, _) => StartCapture();
            tray.OpenRequested += (_, _) => OpenMain(); tray.ExitRequested += (_, _) => ExitApplication();
            tray.PinsRequested += (_, _) => pins.ShowManager();
            tray.DiagnosticsRequested += async (_, _) => { try { await ExportDiagnosticsAsync(); } catch (Exception error) { ShowError(error); } };
            activationListener = ThreadPool.RegisterWaitForSingleObject(activation, (_, _) => Dispatcher.BeginInvoke(OpenMain), null, Timeout.Infinite, false);
            bool registered = hotkey.Register(); diagnostics.Record("app", "hotkey-registration", new { Registered = registered });
            if (!registered)
            {
                ((MainViewModel)main.DataContext).Status = "Ctrl+Alt+A 注册失败，可能已被占用；可通过托盘或主窗口截图。";
                MessageBox.Show("快捷键 Ctrl+Alt+A 无法注册，可能已被其他软件占用。\n可使用托盘菜单或主窗口截图。", "自用截图", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex) { ShowError(ex); Shutdown(1); }
    }

    private async void StartCapture()
    {
        if (!Dispatcher.CheckAccess()) { await Dispatcher.InvokeAsync(StartCapture); return; }
        try { if (coordinator is not null) await coordinator.CaptureAsync(); }
        catch (Exception e) { ShowError(e); }
    }
    private void OpenMain()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(OpenMain); return; }
        if (main is null) return;
        main.Show(); if (main.WindowState == WindowState.Minimized) main.WindowState = WindowState.Normal;
        main.Activate();
    }
    private void ExitApplication()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(ExitApplication); return; }
        foreach (var editor in Windows.OfType<EditorWindow>().ToArray())
        { editor.Close(); if (Windows.Cast<Window>().Contains(editor)) return; }
        coordinator?.Dispose();
        if (main is not null) { main.AllowClose = true; main.Close(); }
        Shutdown();
    }
    private void ShowError(Exception e)
    {
        diagnostics?.Record("app", "operation-failed", error: e);
        MessageBox.Show($"操作失败：{e.Message}\n可从托盘或主窗口导出诊断包。", "自用截图", MessageBoxButton.OK, MessageBoxImage.Error);
    }
    private void RecordUnhandled(Exception error, string stage)
    {
        diagnostics?.Record("app", stage, error: error);
        // Give fatal records a bounded chance to reach disk before the process terminates.
        try { diagnostics?.FlushAsync().Wait(TimeSpan.FromSeconds(1)); } catch { }
    }
    private bool exporting;
    private async Task ExportDiagnosticsAsync()
    {
        if (!Dispatcher.CheckAccess()) { await Dispatcher.InvokeAsync(ExportDiagnosticsAsync).Task.Unwrap(); return; }
        if (exporting || diagnostics is null || desktop is null) return;
        exporting = true;
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { Title = "导出诊断包", Filter = "诊断包 (*.zip)|*.zip", DefaultExt = ".zip",
                FileName = $"自用截图-诊断-{DateTime.Now:yyyyMMdd-HHmmss}.zip" };
            if (dialog.ShowDialog() != true) return;
            diagnostics.Record("diagnostics", "export-requested");
            var environment = await Task.Run(() => DesktopDiagnostics.EnvironmentInfo(desktop));
            await diagnostics.ExportAsync(dialog.FileName, environment);
            if (main?.DataContext is MainViewModel vm) vm.Status = "诊断包已保存。反馈问题时可附上此 ZIP 文件。";
            MessageBox.Show("诊断包已保存。\n包含环境信息、操作耗时和脱敏异常堆栈，不包含截图或识别文本。", "自用截图");
        }
        finally { exporting = false; }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        diagnostics?.Record("app", "exiting", new { e.ApplicationExitCode });
        activationListener?.Unregister(null); coordinator?.Dispose(); pins?.Dispose(); hotkey?.Dispose(); tray?.Dispose();
        if (ownsMutex) instance?.ReleaseMutex(); instance?.Dispose(); activation?.Dispose();
        diagnostics?.Dispose();
        base.OnExit(e);
    }
}
