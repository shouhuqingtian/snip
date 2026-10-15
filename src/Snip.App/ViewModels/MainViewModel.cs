using Snip.App.Mvvm;

namespace Snip.App.ViewModels;

public sealed class MainViewModel(Func<Task> capture, Action<Exception> onError, Func<Task>? exportDiagnostics = null, Action? onlineSettings = null, Action? hotkeySettings = null, Action? applicationSettings = null) : ObservableObject
{
    private string status = "已常驻托盘，随时按 Ctrl+Alt+A 截图。";
    private string hotkeyText = "Ctrl+Alt+A";
    public AsyncCommand CaptureCommand { get; } = new(capture, onError);
    public AsyncCommand ExportDiagnosticsCommand { get; } = new(exportDiagnostics ?? (() => Task.CompletedTask), onError, () => exportDiagnostics is not null);
    public RelayCommand OnlineSettingsCommand { get; } = new(onlineSettings ?? (() => { }), () => onlineSettings is not null);
    public RelayCommand HotkeySettingsCommand { get; } = new(hotkeySettings ?? (() => { }), () => hotkeySettings is not null);
    public RelayCommand ApplicationSettingsCommand { get; } = new(applicationSettings ?? (() => { }), () => applicationSettings is not null);
    public string HotkeyText { get => hotkeyText; set => Set(ref hotkeyText, value); }
    public string VersionText => "版本 " + Services.DesktopDiagnostics.Version.Split('+')[0] + " · 关闭窗口后继续在托盘运行";
    public string Status { get => status; set => Set(ref status, value); }
}
