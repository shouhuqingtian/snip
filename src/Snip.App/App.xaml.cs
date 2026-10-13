using System.Threading;
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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
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
            var renderer = new AnnotationRenderer();
            var export = new ImageExportService();
            pins = new PinManager(export);
            var features = new FeatureHub(renderer, export, capture, pins);
            coordinator = new CaptureCoordinator(capture, renderer, export, features);
            main = new MainWindow { DataContext = new MainViewModel(coordinator.CaptureAsync, ShowError) };
            MainWindow = main;
            hotkey = new HotkeyService(); tray = new TrayService();
            hotkey.Triggered += (_, _) => StartCapture(); tray.CaptureRequested += (_, _) => StartCapture();
            tray.OpenRequested += (_, _) => OpenMain(); tray.ExitRequested += (_, _) => ExitApplication();
            tray.PinsRequested += (_, _) => pins.ShowManager();
            activationListener = ThreadPool.RegisterWaitForSingleObject(activation, (_, _) => Dispatcher.BeginInvoke(OpenMain), null, Timeout.Infinite, false);
            if (!hotkey.Register())
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
    private static void ShowError(Exception e) => MessageBox.Show($"操作失败：{e.Message}", "自用截图", MessageBoxButton.OK, MessageBoxImage.Error);
    protected override void OnExit(ExitEventArgs e)
    {
        activationListener?.Unregister(null); coordinator?.Dispose(); pins?.Dispose(); hotkey?.Dispose(); tray?.Dispose();
        if (ownsMutex) instance?.ReleaseMutex(); instance?.Dispose(); activation?.Dispose();
        base.OnExit(e);
    }
}
