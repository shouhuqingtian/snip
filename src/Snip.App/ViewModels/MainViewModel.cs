using Snip.App.Mvvm;

namespace Snip.App.ViewModels;

public sealed class MainViewModel(Func<Task> capture, Action<Exception> onError) : ObservableObject
{
    private string status = "已常驻托盘，随时按 Ctrl+Alt+A 截图。";
    public AsyncCommand CaptureCommand { get; } = new(capture, onError);
    public string Status { get => status; set => Set(ref status, value); }
}
