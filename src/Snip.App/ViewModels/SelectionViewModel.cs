using Snip.App.Mvvm;
using Snip.Core;
using Snip.Infrastructure;

namespace Snip.App.ViewModels;

public sealed class SelectionViewModel(DesktopFrame frame) : ObservableObject
{
    private PixelRect selection;
    private PixelRect dragOriginal;
    private PixelPoint dragStart;
    private SelectionHandle handle;
    public DesktopFrame Frame { get; } = frame;
    public PixelRect Selection { get => selection; private set { if (Set(ref selection, value)) { Notify(nameof(SizeText)); Notify(nameof(CanConfirm)); Changed?.Invoke(this, EventArgs.Empty); } } }
    public bool IsDragging { get; private set; }
    public bool CanConfirm => !Selection.IsEmpty && !IsDragging;
    public string SizeText => Selection.IsEmpty ? "拖动选择截图区域" : $"{Selection.RoundOut().Width:0} × {Selection.RoundOut().Height:0} 像素";
    public event EventHandler? Changed;

    public void Begin(PixelPoint point)
    {
        dragStart = point; dragOriginal = Selection;
        handle = SelectionGeometry.HitTest(Selection, point);
        IsDragging = true; Notify(nameof(CanConfirm));
        if (handle == SelectionHandle.New) Selection = new(point.X, point.Y, 0, 0);
    }
    public void Move(PixelPoint point)
    {
        if (IsDragging) Selection = SelectionGeometry.Drag(dragOriginal, handle, dragStart, point, Frame.Bounds);
    }
    public void End(PixelPoint point)
    {
        Move(point); IsDragging = false; Selection = Selection.RoundOut().Intersect(Frame.Bounds);
        Notify(nameof(CanConfirm)); Changed?.Invoke(this, EventArgs.Empty);
    }
    public void CancelDrag() { if (!IsDragging) return; IsDragging = false; Selection = dragOriginal; Notify(nameof(CanConfirm)); }
}
