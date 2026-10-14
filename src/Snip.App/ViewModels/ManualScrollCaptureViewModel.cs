using Snip.App.Mvvm;
using Snip.Core;

namespace Snip.App.ViewModels;

public sealed class ManualScrollCaptureViewModel : ObservableObject
{
    private string status = "正在准备网页区域…";
    private bool ready, canFinish;
    private int frames, height;
    public ManualScrollCaptureViewModel(ManualCaptureControl control, Action cancel)
    {
        CompleteCommand = new(control.RequestFinish, () => CanFinish);
        CancelCommand = new(cancel);
    }
    public string Status { get => status; private set => Set(ref status, value); }
    public bool Ready { get => ready; private set => Set(ref ready, value); }
    public bool CanFinish { get => canFinish; private set { if (Set(ref canFinish, value)) CompleteCommand.Refresh(); } }
    public int Frames { get => frames; private set => Set(ref frames, value); }
    public int Height { get => height; private set => Set(ref height, value); }
    public RelayCommand CompleteCommand { get; }
    public RelayCommand CancelCommand { get; }
    public void Update(ManualCaptureProgress value)
    { Frames = value.Frames; Height = value.Height; Status = value.Status; Ready = true; CanFinish = value.CanFinish; }
}
