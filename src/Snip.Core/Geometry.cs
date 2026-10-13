namespace Snip.Core;

public readonly record struct PixelPoint(double X, double Y)
{
    public static PixelPoint operator +(PixelPoint a, PixelPoint b) => new(a.X + b.X, a.Y + b.Y);
    public static PixelPoint operator -(PixelPoint a, PixelPoint b) => new(a.X - b.X, a.Y - b.Y);
}

public readonly record struct PixelRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public bool Contains(PixelPoint p) => p.X >= X && p.X <= Right && p.Y >= Y && p.Y <= Bottom;
    public static PixelRect FromPoints(PixelPoint a, PixelPoint b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    public PixelRect Intersect(PixelRect other)
    {
        double x = Math.Max(X, other.X), y = Math.Max(Y, other.Y);
        return new(x, y, Math.Max(0, Math.Min(Right, other.Right) - x), Math.Max(0, Math.Min(Bottom, other.Bottom) - y));
    }
    public PixelRect Translate(PixelPoint delta) => this with { X = X + delta.X, Y = Y + delta.Y };
    public PixelRect ClampInside(PixelRect bounds) => new(
        Math.Clamp(X, bounds.X, bounds.Right - Math.Min(Width, bounds.Width)),
        Math.Clamp(Y, bounds.Y, bounds.Bottom - Math.Min(Height, bounds.Height)),
        Math.Min(Width, bounds.Width), Math.Min(Height, bounds.Height));
    public PixelRect RoundOut() => new(Math.Floor(X), Math.Floor(Y), Math.Ceiling(Right) - Math.Floor(X), Math.Ceiling(Bottom) - Math.Floor(Y));
}

public enum SelectionHandle { New, Move, Left, Right, Top, Bottom, TopLeft, TopRight, BottomLeft, BottomRight }

public static class SelectionGeometry
{
    public static SelectionHandle HitTest(PixelRect rect, PixelPoint point, double tolerance = 8)
    {
        if (rect.IsEmpty) return SelectionHandle.New;
        bool l = Math.Abs(point.X - rect.X) <= tolerance, r = Math.Abs(point.X - rect.Right) <= tolerance;
        bool t = Math.Abs(point.Y - rect.Y) <= tolerance, b = Math.Abs(point.Y - rect.Bottom) <= tolerance;
        if (l && t) return SelectionHandle.TopLeft;
        if (r && t) return SelectionHandle.TopRight;
        if (l && b) return SelectionHandle.BottomLeft;
        if (r && b) return SelectionHandle.BottomRight;
        if (point.Y >= rect.Y && point.Y <= rect.Bottom) { if (l) return SelectionHandle.Left; if (r) return SelectionHandle.Right; }
        if (point.X >= rect.X && point.X <= rect.Right) { if (t) return SelectionHandle.Top; if (b) return SelectionHandle.Bottom; }
        return rect.Contains(point) ? SelectionHandle.Move : SelectionHandle.New;
    }

    public static PixelRect Drag(PixelRect original, SelectionHandle handle, PixelPoint start, PixelPoint current, PixelRect bounds)
    {
        current = new(Math.Clamp(current.X, bounds.X, bounds.Right), Math.Clamp(current.Y, bounds.Y, bounds.Bottom));
        if (handle == SelectionHandle.New) return PixelRect.FromPoints(start, current).Intersect(bounds);
        if (handle == SelectionHandle.Move) return original.Translate(current - start).ClampInside(bounds);
        double l = original.X, r = original.Right, t = original.Y, b = original.Bottom;
        if (handle is SelectionHandle.Left or SelectionHandle.TopLeft or SelectionHandle.BottomLeft) l = current.X;
        if (handle is SelectionHandle.Right or SelectionHandle.TopRight or SelectionHandle.BottomRight) r = current.X;
        if (handle is SelectionHandle.Top or SelectionHandle.TopLeft or SelectionHandle.TopRight) t = current.Y;
        if (handle is SelectionHandle.Bottom or SelectionHandle.BottomLeft or SelectionHandle.BottomRight) b = current.Y;
        return PixelRect.FromPoints(new(l, t), new(r, b)).Intersect(bounds);
    }
}
