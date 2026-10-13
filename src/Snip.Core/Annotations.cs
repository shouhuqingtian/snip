namespace Snip.Core;

public enum AnnotationTool { Select, Rectangle, Ellipse, Arrow, Pen, Text, Mosaic, Sticker }

/// <summary>All positions and widths are image pixels, independent of display DPI.</summary>
public sealed record Annotation(
    Guid Id, AnnotationTool Tool, PixelPoint Start, PixelPoint End,
    string Color, double StrokeWidth, double FontSize, int MosaicSize,
    string Text, IReadOnlyList<PixelPoint> Points)
{
    public bool Filled { get; init; }
    public ReadOnlyMemory<byte> StickerPng { get; init; }
    public PixelRect Bounds => Tool == AnnotationTool.Pen && Points.Count > 0
        ? new(Points.Min(p => p.X), Points.Min(p => p.Y), Points.Max(p => p.X) - Points.Min(p => p.X), Points.Max(p => p.Y) - Points.Min(p => p.Y))
        : PixelRect.FromPoints(Start, End);

    public Annotation Translate(PixelPoint delta) => this with
    {
        Start = Start + delta, End = End + delta,
        Points = Points.Select(p => p + delta).ToArray()
    };

    public Annotation Resize(PixelRect original, PixelRect target)
    {
        double sx = target.Width / Math.Max(1, original.Width), sy = target.Height / Math.Max(1, original.Height);
        PixelPoint Map(PixelPoint p) => new(target.X + (p.X - original.X) * sx, target.Y + (p.Y - original.Y) * sy);
        return this with { Start = Map(Start), End = Map(End), Points = Points.Select(Map).ToArray(),
            FontSize = Tool == AnnotationTool.Text ? Math.Clamp(FontSize * Math.Min(sx, sy), 8, 300) : FontSize };
    }
}

/// <summary>History stores immutable annotation lists; uncommitted pointer gestures never enter history.</summary>
public sealed class EditHistory
{
    private readonly List<IReadOnlyList<Annotation>> states = [Array.AsReadOnly(Array.Empty<Annotation>())];
    private int position;
    private IReadOnlyList<Annotation>? exported;
    public IReadOnlyList<Annotation> Current => states[position];
    public bool CanUndo => position > 0;
    public bool CanRedo => position < states.Count - 1;
    public bool IsDirty => exported is null || !ReferenceEquals(exported, states[position]);

    public EditHistory() => exported = states[0];
    public void Commit(IEnumerable<Annotation> annotations)
    {
        // Reuse our already-frozen annotations; copy only new/replaced data.
        var known = new HashSet<Annotation>(Current, ReferenceEqualityComparer.Instance);
        var next = annotations.Select(a => known.Contains(a) ? a : a with
        {
            Points = Array.AsReadOnly(a.Points.ToArray()),
            StickerPng = a.StickerPng.IsEmpty || Current.Any(old => old.StickerPng.Equals(a.StickerPng)) ? a.StickerPng : a.StickerPng.ToArray()
        }).ToArray();
        states.RemoveRange(position + 1, states.Count - position - 1);
        states.Add(Array.AsReadOnly(next));
        position++;
        if (states.Count > 101) { states.RemoveAt(0); position--; }
    }
    public void Undo() { if (CanUndo) position--; }
    public void Redo() { if (CanRedo) position++; }
    public void MarkExported() => exported = states[position];
}
