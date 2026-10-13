namespace Snip.Core;

public sealed record PixelFrame(int Width, int Height, byte[] Bgra)
{
    public byte At(int x, int y, int channel) => Bgra[(y * Width + x) * 4 + channel];
}
public sealed record ScrollMatch(int Offset, int Header, int Footer, double Error);

/// <summary>Match moving edges rather than letting empty backgrounds and fixed UI dominate.</summary>
public static class ScrollMatching
{
    public static double Difference(PixelFrame a, PixelFrame b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return double.PositiveInfinity;
        double error = 0; int count = 0;
        for (int y = 0; y < a.Height; y += 7) for (int x = 0; x < a.Width; x += 11) for (int c = 0; c < 3; c++)
        { error += Math.Abs(a.At(x, y, c) - b.At(x, y, c)); count++; }
        return error / Math.Max(1, count);
    }
    public static bool Unchanged(PixelFrame a, PixelFrame b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return false;
        int changed = 0;
        for (int y = 0; y < a.Height; y += 3) for (int x = 0; x < a.Width; x += 3)
        {
            int error = 0;
            for (int c = 0; c < 3; c++) error += Math.Abs(a.At(x, y, c) - b.At(x, y, c));
            if (error > 36 && ++changed >= 8) return false;
        }
        return true;
    }
    public static ScrollMatch? Match(PixelFrame previous, PixelFrame next)
    {
        if (previous.Width != next.Width || previous.Height != next.Height || previous.Height < 120) return null;
        var features = new List<(int X, int Y)>();
        // Keep spatially distributed anchors. Ignore patches unchanged at the same screen
        // position (fixed sidebars, floating buttons), and flat white/grey margins.
        for (int top = 3; top < previous.Height - 3; top += 24)
        for (int left = 3; left < previous.Width - 3; left += 32)
        {
            int selected = 0;
            for (int y = top; y < Math.Min(top + 24, previous.Height - 3) && selected < 4; y += 3)
            for (int x = left; x < Math.Min(left + 32, previous.Width - 3) && selected < 4; x += 3)
            {
                int contrast = 0, change = 0, centerChange = 0;
                for (int c = 0; c < 3; c++)
                {
                    contrast += Math.Abs(previous.At(x, y, c) - previous.At(x + 2, y, c)) + Math.Abs(previous.At(x, y, c) - previous.At(x, y + 2, c));
                    change += Math.Abs(previous.At(x, y, c) - next.At(x, y, c)) + Math.Abs(previous.At(x + 2, y + 2, c) - next.At(x + 2, y + 2, c));
                    centerChange += Math.Abs(previous.At(x, y, c) - next.At(x, y, c));
                }
                if (contrast >= 45 && change >= 30 && centerChange >= 18) { features.Add((x, y)); selected++; }
            }
        }
        if (features.Count < 8) return null;
        var candidates = new List<(int Shift, double Error, int Count)>();
        for (int shift = 1; shift < previous.Height * .8; shift++)
        {
            double sum = 0; int count = 0;
            foreach (var (x, y) in features)
            {
                if (y - shift < 3) continue;
                double error = 0;
                for (int c = 0; c < 3; c++)
                {
                    error += Math.Abs(previous.At(x, y, c) - next.At(x, y - shift, c));
                    error += Math.Abs(previous.At(x + 2, y + 2, c) - next.At(x + 2, y + 2 - shift, c));
                    error += Math.Abs(previous.At(x - 2, y - 2, c) - next.At(x - 2, y - 2 - shift, c));
                }
                sum += Math.Min(64, error / 9); count++;
            }
            if (count >= Math.Max(8, features.Count / 5)) candidates.Add((shift, sum / count, count));
        }
        if (candidates.Count == 0) return null;
        var ordered = candidates.OrderBy(c => c.Error).ToArray(); var best = ordered[0];
        double alternative = ordered.Where(c => Math.Abs(c.Shift - best.Shift) > 4).Select(c => c.Error).DefaultIfEmpty(64).Min();
        if (best.Error > 12 || alternative - best.Error < 1.2) return null;
        var (header, footer) = FixedBands(previous, next);
        return new(best.Shift, header, footer, best.Error);
    }
    public static (int Header, int Footer) FixedBands(PixelFrame previous, PixelFrame next)
    {
        bool Same(int row)
        {
            int error = 0;
            for (int x = 3; x < previous.Width - 3; x += 5) for (int c = 0; c < 3; c++)
            { error += Math.Abs(previous.At(x, row, c) - next.At(x, row, c)); if (error > 6) return false; }
            return true;
        }
        int header = 0, footer = 0;
        while (header < previous.Height / 5 && Same(header)) header++;
        while (footer < previous.Height / 5 && Same(previous.Height - footer - 1)) footer++;
        return (header, footer);
    }
}
