using System.Threading;
using System.IO;
using System.Windows;
using Snip.App.Services;
using Snip.App.ViewModels;
using Snip.App.Views;
using Snip.Core;
using Snip.Infrastructure;
using Snip.Ocr;

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
    private PinHotkeyService? pinHotkeys;
    private PinHotkeySettingsStore? pinHotkeyStore;
    private PinHotkeySettingsWindow? pinHotkeySettings;
    private MainWindow? main;
    private bool ownsMutex;
    private DiagnosticJournal? diagnostics;
    private IDesktopCaptureService? desktop;
    private IHotkeySettingsStore? hotkeyStore;
    private HotkeySettingsWindow? hotkeySettings;
    private ApplicationSettingsWindow? applicationSettings;
    private TranslationPreferencesStore? translationPreferences;
    private FeatureHub? features;
    private CopyNotificationService? copyNotifications;
    private ConfigurationTransferWindow? configurationTransfer;
    private AccountSyncService? accountSync;
    private AccountSyncWindow? accountSyncWindow;
    private TranscriptionSettingsStore? transcriptionSettings;
    private VoiceInputController? voiceInput;
    private VoiceInputSettingsWindow? voiceInputSettings;
    private VoiceInputStatusWindow? voiceInputStatus;
    private VoiceInputResultWindow? voiceInputResult;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length == 1 && e.Args[0] == "--voice-target-worker")
        { int result = await Task.Run(() => VoiceTargetWorker.Run(Console.In, Console.Out)); Shutdown(result); return; }
        if (e.Args.Length == 1 && e.Args[0] == "--voice-record-worker")
        { int result = await VoiceRecordingWorker.RunAsync(Console.In, Console.OpenStandardOutput()); Shutdown(result); return; }
        if (e.Args.Length == 2 && e.Args[0] == "--ocr-worker") { RunOcrWorker(e.Args[1]); return; }
        if (e.Args.Length == 1 && e.Args[0] == "--hover-worker") { RunHoverWorker(); return; }
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
        bool startupLaunch = e.Args.Contains("--startup", StringComparer.Ordinal);
        activation = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\SelfSnip.Activate.v1");
        instance = new Mutex(true, @"Local\SelfSnip.Instance.v1", out ownsMutex);
        if (!ownsMutex)
        {
            if (startupLaunch) { Shutdown(); return; }
            activation.Set();
            MessageBox.Show($"已有自用截图在运行，已打开它的主窗口。\n此次启动的文件版本为 {DesktopDiagnostics.Version.Split('+')[0]}。\n如果刚更新软件，请先在旧实例的托盘菜单选择‘退出’，再运行新版 Snip.exe。",
                "自用截图 · 已有实例运行", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(); return;
        }
        try
        {
            await Task.Run(() => new SettingsConfigurationTransfer(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SelfSnip")).RecoverInterruptedImport());
            await Task.Run(() => new SettingsConfigurationTransfer(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SelfSnip"), includeDeviceSettings: false).RecoverInterruptedImport());
            // Explicit constructor injection keeps Windows concerns outside ViewModels.
            var capture = new DesktopCaptureService();
            desktop = capture;
            _ = Task.Run(() => diagnostics.Record("app", "environment", DesktopDiagnostics.EnvironmentInfo(capture)));
            var renderer = new AnnotationRenderer();
            copyNotifications = new CopyNotificationService(Dispatcher, diagnostics);
            var export = new CopyFeedbackExport(new DiagnosticImageExport(new ImageExportService(), diagnostics), copyNotifications);
            pins = new PinManager(export, ShowPinHotkeySettings);
            translationPreferences = new TranslationPreferencesStore();
            features = new FeatureHub(renderer, export, capture, pins, diagnostics, translationPreferences:translationPreferences);
            transcriptionSettings = new TranscriptionSettingsStore(new OnlineTranslationSettingsStore());
            voiceInput = new VoiceInputController(transcriptionSettings, Dispatcher, diagnostics);
            voiceInput.CanStart = () => !Windows.Cast<Window>().Any(w => w is VoiceInputSettingsWindow { IsActive: true } or HotkeySettingsWindow { IsActive: true } or PinHotkeySettingsWindow { IsActive: true });
            voiceInput.ShortcutWhileConfiguring += gesture => { if (voiceInputSettings?.IsActive == true) voiceInputSettings.CaptureRegisteredGesture(gesture); };
            accountSync = new AccountSyncService(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SelfSnip"), Dispatcher,
                () => voiceInput?.IsActive != true && !Windows.Cast<Window>().Any(window => window is OnlineTranslationSettingsWindow or OnlineSpeechSettingsWindow or VoiceInputSettingsWindow or ApplicationSettingsWindow or ConfigurationTransferWindow),
                features.RefreshAccountConfiguration, diagnostics);
            await accountSync.StartAsync();
            coordinator = new CaptureCoordinator(capture, renderer, export, features, diagnostics, () => { copyNotifications.Hide(); voiceInput?.Cancel(true); voiceInputStatus?.Close(); });
            main = new MainWindow { Title = "自用截图 " + DesktopDiagnostics.Version.Split('+')[0], DataContext = new MainViewModel(coordinator.CaptureAsync, ShowError, ExportDiagnosticsAsync, () => features.ShowOnlineSettings(), ShowHotkeySettings, ShowApplicationSettings, ShowConfigurationTransfer, ShowAccountSync, ShowVoiceInputSettings) };
            MainWindow = main;
            hotkey = new HotkeyService(); var desktopTray = new TrayService(); tray = desktopTray;
            desktopTray.VoiceSettingsRequested += (_, _) => ShowVoiceInputSettings();
            desktopTray.VoiceResultRequested += (_, _) => ShowVoiceInputResult();
            hotkeyStore = new HotkeySettingsStore();
            features.CaptureStatus = message => tray?.SetCaptureStatus(message);
            tray.CaptureCancelRequested += (_, _) => features.CancelBrowserCapture();
            hotkey.Triggered += (_, _) =>
            {
                if (voiceInputSettings?.IsActive == true) { voiceInputSettings.CaptureRegisteredGesture(hotkey.Current ?? HotkeyGesture.Default); return; }
                if (hotkeySettings?.IsActive != true && pinHotkeySettings?.IsActive != true) StartCapture();
            }; tray.CaptureRequested += (_, _) => StartCapture();
            tray.OpenRequested += (_, _) => OpenMain(); tray.ExitRequested += (_, _) => ExitApplication();
            tray.PinsRequested += (_, _) => pins.ShowManager();
            pinHotkeys = new PinHotkeyService(); pinHotkeyStore = new PinHotkeySettingsStore();
            pinHotkeys.Triggered += (_, action) =>
            {
                if (voiceInputSettings?.IsActive == true)
                {
                    var gesture = action == PinHotkeyAction.Restore ? pinHotkeys.Current.Restore : action == PinHotkeyAction.Previous ? pinHotkeys.Current.Previous : pinHotkeys.Current.Next;
                    if (gesture is not null) voiceInputSettings.CaptureRegisteredGesture(gesture); return;
                }
                if (pinHotkeySettings?.IsActive == true || hotkeySettings?.IsActive == true) return;
                try { if (action == PinHotkeyAction.Restore) pins.RestoreLast(); else pins.Cycle(action == PinHotkeyAction.Previous ? -1 : 1); }
                catch (Exception error) { ShowError(error); }
            };
            try
            {
                bool available = pinHotkeys.Apply(pinHotkeyStore.Read());
                diagnostics.Record("pin-hotkeys", "startup-registration", new { Registered = available });
                if (!available) pins.ViewModel.Status = "贴图快捷键已被占用，请点击快捷键设置修改；贴图管理按钮仍可使用。";
            }
            catch (Exception error) { diagnostics.Record("pin-hotkeys", "startup-registration-failed", error: error); pins.ViewModel.Status = "贴图快捷键配置读取或注册失败，可点击快捷键设置重新保存。"; }

            tray.DiagnosticsRequested += async (_, _) => { try { await ExportDiagnosticsAsync(); } catch (Exception error) { ShowError(error); } };
            activationListener = ThreadPool.RegisterWaitForSingleObject(activation, (_, _) => Dispatcher.BeginInvoke(OpenMain), null, Timeout.Infinite, false);
            HotkeyGesture configured = HotkeyGesture.Default;
            bool readFailed = false;
            try { configured = hotkeyStore.Read(); }
            catch (Exception error) { diagnostics.Record("app", "hotkey-settings-read-failed", error: error); readFailed = true; }
            UpdateHotkey(configured);
            if (readFailed) ((MainViewModel)main.DataContext).Status = "快捷键配置读取失败，本次采用 Ctrl+Alt+A；可重新保存设置。";
            bool registered = hotkey.Register(configured); diagnostics.Record("app", "hotkey-registration", new { Registered = registered });
            if (!registered)
            {
                ((MainViewModel)main.DataContext).Status = configured.DisplayText + " 注册失败，可点‘修改’换一个组合，或通过托盘截图。";
                if (!startupLaunch) MessageBox.Show($"快捷键 {configured.DisplayText} 无法注册，可能已被其他软件占用。\n可打开主窗口修改快捷键，或使用托盘菜单截图。", "自用截图", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            try { voiceInput.Initialize(); }
            catch (Exception error) { diagnostics.Record("voice-input", "startup-registration-failed", error: error); ((MainViewModel)main.DataContext).Status = "语音输入暂不可用，请打开语音输入设置检查快捷键和密钥；截图功能仍可使用。"; }
            voiceInput.Changed += VoiceInputChanged;
        }
        catch (Exception ex) { ShowError(ex); Shutdown(1); }
    }

    private async void RunOcrWorker(string folder)
    {
        int code;
        try { code = await Task.Run(() => OcrWorker.RunAsync(folder, Console.OpenStandardInput(), Console.OpenStandardOutput())); }
        catch { code = 6; }
        Shutdown(code);
    }
    private async void RunHoverWorker()
    {
        int code;
        try { code = await Task.Run(() => HoverRegionWorker.Run(Console.OpenStandardInput(), Console.OpenStandardOutput())); }
        catch { code = 6; }
        Shutdown(code);
    }

    private void UpdateHotkey(HotkeyGesture gesture)
    {
        tray?.SetHotkey(gesture);
        if (main?.DataContext is MainViewModel vm) { vm.HotkeyText = gesture.DisplayText; vm.Status = "已常驻托盘，按 " + gesture.DisplayText + " 截图。"; }
    }
    private void ShowHotkeySettings()
    {
        if (hotkeySettings is not null) { hotkeySettings.Activate(); return; }
        if (hotkey is null || hotkeyStore is null) return;
        var vm = new HotkeySettingsViewModel(hotkey, hotkeyStore, hotkey.Current ?? HotkeyGesture.Default);
        vm.Saved += (_, gesture) => UpdateHotkey(gesture);
        hotkeySettings = new HotkeySettingsWindow(vm) { Owner = main?.IsVisible == true ? main : null };
        hotkeySettings.Closed += (_, _) => hotkeySettings = null;
        hotkeySettings.Show();
    }
    private void ShowVoiceInputSettings()
    {
        if (voiceInputSettings is not null) { voiceInputSettings.Activate(); return; }
        if (transcriptionSettings is null || voiceInput is null) return;
        voiceInput.Cancel(true); voiceInputStatus?.Close();
        try
        {
            var vm = new VoiceInputSettingsViewModel(transcriptionSettings, new OnlineTranslationSettingsStore(), voiceInput);
            voiceInputSettings = new(vm); voiceInputSettings.Closed += (_, _) => voiceInputSettings = null; voiceInputSettings.Show();
        }
        catch (Exception error) { ShowError(error); }
    }
    private void VoiceInputChanged(object? sender, EventArgs args)
    {
        if (voiceInput is null || Dispatcher.HasShutdownStarted) return;
        if (voiceInput.SuppressNotification) { voiceInputStatus?.Close(); return; }
        try
        {
            if (voiceInputStatus is null)
            {
                var window = new VoiceInputStatusWindow(voiceInput, ShowVoiceInputResult); voiceInputStatus = window;
                window.Closed += (_, _) => { if (ReferenceEquals(voiceInputStatus, window)) voiceInputStatus = null; }; window.Show();
            }
            else voiceInputStatus.UpdateStatus();
        }
        catch (Exception error)
        {
            diagnostics?.Record("voice-input", "status-window-failed", error: error); voiceInput.Cancel(true); voiceInputStatus?.Close(); voiceInputStatus = null;
            if (main?.DataContext is MainViewModel vm) vm.Status = "语音提示无法显示，本次语音输入已取消，可导出诊断包排查。";
        }
    }
    private void ShowVoiceInputResult()
    {
        if (voiceInput?.LastText is not { Length: > 0 } text) { if (main?.DataContext is MainViewModel vm) vm.Status = "还没有语音识别结果；先配置语音输入，再把光标放到输入框并按所选方式录音。"; OpenMain(); return; }
        voiceInputResult?.Close(); voiceInputResult = new(text); voiceInputResult.Closed += (_, _) => voiceInputResult = null; voiceInputResult.Show();
    }

    private void ShowPinHotkeySettings()
    {
        if (pinHotkeySettings is not null) { pinHotkeySettings.Activate(); return; }
        if (pinHotkeys is null || pinHotkeyStore is null) return;
        PinHotkeySettings configured;
        try { configured = pinHotkeyStore.Read(); }
        catch (Exception error) { diagnostics?.Record("pin-hotkeys", "settings-read-failed", error: error); configured = pinHotkeys.Current; }
        var vm = new PinHotkeySettingsViewModel(pinHotkeys, pinHotkeyStore, configured);
        pinHotkeySettings = new PinHotkeySettingsWindow(vm) { Owner = Windows.OfType<PinManagerWindow>().FirstOrDefault(window => window.IsVisible) };
        pinHotkeySettings.Closed += (_, _) => pinHotkeySettings = null;
        pinHotkeySettings.Show();
    }

    private void ShowApplicationSettings()
    {
        if (applicationSettings is not null) { applicationSettings.Activate(); return; }
        if (translationPreferences is null || features is null) return;
        var vm = new ApplicationSettingsViewModel(new AutoStartService(), translationPreferences, features.DefaultTranslationPreferences());
        applicationSettings = new ApplicationSettingsWindow(vm) { Owner = main?.IsVisible == true ? main : null };
        applicationSettings.Closed += (_, _) => applicationSettings = null;
        applicationSettings.Show();
    }

    private void ShowAccountSync()
    {
        if (accountSync is null) return;
        if (accountSyncWindow is not null) { accountSyncWindow.Show(); accountSyncWindow.Activate(); return; }
        accountSyncWindow = new AccountSyncWindow(new AccountSyncViewModel(accountSync, Dispatcher));
        accountSyncWindow.Closed += (_, _) => accountSyncWindow = null;
        accountSyncWindow.Show(); accountSyncWindow.Activate();
    }

    private void ShowConfigurationTransfer()
    {
        if (configurationTransfer is not null) { configurationTransfer.Activate(); return; }
        var transfer = new SettingsConfigurationTransfer(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SelfSnip"));
        string? ChooseExport()
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { Title = "导出加密配置", Filter = "加密配置 (*.snipconfig)|*.snipconfig", DefaultExt = ".snipconfig", FileName = $"自用截图-配置-{DateTime.Now:yyyyMMdd-HHmmss}.snipconfig" };
            return dialog.ShowDialog(configurationTransfer) == true ? dialog.FileName : null;
        }
        string? ChooseImport()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = "导入加密配置", Filter = "加密配置 (*.snipconfig)|*.snipconfig", DefaultExt = ".snipconfig", CheckFileExists = true };
            return dialog.ShowDialog(configurationTransfer) == true ? dialog.FileName : null;
        }
        var vm = new ConfigurationTransferViewModel(transfer, ChooseExport, ChooseImport,
            () => !Windows.Cast<Window>().Any(window => window is OnlineTranslationSettingsWindow or OnlineSpeechSettingsWindow or ApplicationSettingsWindow or HotkeySettingsWindow or PinHotkeySettingsWindow or TextResultWindow),
            () => { if (main?.DataContext is MainViewModel model) model.Status = "配置已导入，请从托盘退出并重启软件，使全部设置生效。"; },
            error => diagnostics?.Record("configuration-transfer", "operation-failed", error: error));
        configurationTransfer = new ConfigurationTransferWindow(vm) { Owner = main?.IsVisible == true ? main : null };
        configurationTransfer.Closed += (_, _) => configurationTransfer = null;
        configurationTransfer.Show();
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
        accountSync?.Dispose();
        voiceInput?.Dispose(); voiceInputStatus?.Close(); voiceInputResult?.Close();
        activationListener?.Unregister(null); copyNotifications?.Dispose(); coordinator?.Dispose(); pins?.Dispose(); pinHotkeys?.Dispose(); hotkey?.Dispose(); tray?.Dispose();
        if (ownsMutex) instance?.ReleaseMutex(); instance?.Dispose(); activation?.Dispose();
        diagnostics?.Dispose();
        base.OnExit(e);
    }
}
