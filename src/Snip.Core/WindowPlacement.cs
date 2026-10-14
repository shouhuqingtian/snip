namespace Snip.Core;

/// <summary>Coordinates and sizes are physical pixels; DPI is used only for a small safe margin.</summary>
public static class WindowPlacement
{
    public static PixelRect InsetWorkArea(PixelRect workArea, double scale)
    {
        if (workArea.IsEmpty || !double.IsFinite(scale) || scale <= 0) throw new ArgumentOutOfRangeException(nameof(workArea));
        double margin = Math.Min(Math.Ceiling(8*scale), Math.Max(0,(Math.Min(workArea.Width,workArea.Height)-1)/2));
        return new(workArea.X+margin,workArea.Y+margin,workArea.Width-2*margin,workArea.Height-2*margin);
    }
    public static PixelRect Fit(PixelRect bounds,PixelRect workArea,double scale,bool center = false)
    {
        var available = InsetWorkArea(workArea,scale);
        double width=Math.Max(1,Math.Min(Math.Ceiling(bounds.Width),Math.Floor(available.Width)));
        double height=Math.Max(1,Math.Min(Math.Ceiling(bounds.Height),Math.Floor(available.Height)));
        double x=center ? available.X+(available.Width-width)/2 : Math.Clamp(bounds.X,available.X,available.Right-width);
        double y=center ? available.Y+(available.Height-height)/2 : Math.Clamp(bounds.Y,available.Y,available.Bottom-height);
        return new(Math.Floor(x),Math.Floor(y),width,height);
    }
}
