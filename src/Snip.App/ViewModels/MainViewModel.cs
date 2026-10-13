using Snip.App.Mvvm;

namespace Snip.App.ViewModels;

public sealed class MainViewModel(Func<Task> capture, Action<Exception> onError, Func<Task>? exportDiagnostics = null) : ObservableObject
{
    private string status = "已常驻托盘，随时按 Ctrl+Alt+A 截图。";
    public AsyncCommand CaptureCommand { get; } = new(capture, onError);
    public AsyncCommand ExportDiagnosticsCommand { get; } = new(exportDiagnostics ?? (() => Task.CompletedTask), onError, () => exportDiagnostics is not null);
    public string VersionText => "版本 " + Services.DesktopDiagnostics.Version.Split('+')[0] + " · 关闭窗口后继续在托盘运行";
    public string Status { get => status; set => Set(ref status, value); }
}
