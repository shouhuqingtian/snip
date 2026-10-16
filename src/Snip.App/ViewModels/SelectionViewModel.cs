using Snip.App.Mvvm;
using Snip.Core;
using Snip.Infrastructure;

namespace Snip.App.ViewModels;

public sealed class SelectionViewModel(DesktopFrame frame) : ObservableObject
{
    private PixelRect selection;
    private PixelRect hoveredSelection;
    private PixelRect dragOriginal;
    private PixelPoint dragStart;
    private SelectionHandle handle;
    public DesktopFrame Frame { get; } = frame;
    public PixelRect Selection { get => selection; private set { if (Set(ref selection, value)) SelectionChanged(); } }
    public PixelRect HoveredSelection { get => hoveredSelection; private set { if (Set(ref hoveredSelection, value)) SelectionChanged(); } }
    public PixelRect DisplayedSelection => Selection.IsEmpty && !IsDragging ? HoveredSelection : Selection;
    public bool IsDragging { get; private set; }
    public bool CanConfirm => !Selection.IsEmpty && !IsDragging;
    public string SizeText => DisplayedSelection.IsEmpty ? "拖动选择截图区域" : $"{DisplayedSelection.RoundOut().Width:0} × {DisplayedSelection.RoundOut().Height:0} 像素";
    public event EventHandler? Changed;

    private void SelectionChanged()
    { Notify(nameof(DisplayedSelection)); Notify(nameof(SizeText)); Notify(nameof(CanConfirm)); Changed?.Invoke(this, EventArgs.Empty); }

    public void Begin(PixelPoint point)
    {
        dragStart = point; dragOriginal = Selection;
        handle = SelectionGeometry.HitTest(Selection, point);
        IsDragging = true; SelectionChanged();
        if (handle == SelectionHandle.New) Selection = new(point.X, point.Y, 0, 0);
    }
    public void Move(PixelPoint point)
    {
        if (IsDragging) Selection = SelectionGeometry.Drag(dragOriginal, handle, dragStart, point, Frame.Bounds);
    }
    public void End(PixelPoint point)
    {
        Move(point); IsDragging = false; Selection = Selection.RoundOut().Intersect(Frame.Bounds);
        SelectionChanged();
    }
    public void CancelDrag() { if (!IsDragging) return; IsDragging = false; Selection = dragOriginal; SelectionChanged(); }
    public void SetSelection(PixelRect rect) => Selection = rect.RoundOut().Intersect(Frame.Bounds);
    public void SetHoveredSelection(PixelRect rect) => HoveredSelection = rect.RoundOut().Intersect(Frame.Bounds);
    public void Nudge(double dx, double dy) { if (!IsDragging) Selection = Selection.Translate(new(dx, dy)).ClampInside(Frame.Bounds); }
}
