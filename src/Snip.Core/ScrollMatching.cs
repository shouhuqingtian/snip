namespace Snip.Core;

public sealed record PixelFrame(int Width, int Height, byte[] Bgra)
{
    public byte At(int x, int y, int channel) => Bgra[(y * Width + x) * 4 + channel];
}
public sealed record ScrollMatch(int Offset, int Header, int Footer, double Error);

/// <summary>Estimates vertical motion using overlapping pixels, rejecting ambiguous repeated content.</summary>
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
    public static ScrollMatch? Match(PixelFrame previous, PixelFrame next)
    {
        if (previous.Width != next.Width || previous.Height != next.Height || previous.Height < 120) return null;
        double RowError(int row)
        {
            double error = 0; int count = 0;
            for (int x = 8; x < previous.Width - 8; x += 13) for (int c = 0; c < 3; c++)
            { error += Math.Abs(previous.At(x, row, c) - next.At(x, row, c)); count++; }
            return error / Math.Max(1, count);
        }
        int header = 0, footer = 0;
        while (header < previous.Height / 5 && RowError(header) < 1) header++;
        while (footer < previous.Height / 5 && RowError(previous.Height - footer - 1) < 1) footer++;
        var candidates = new List<(int Shift, double Error)>();
        for (int shift = 8; shift < (previous.Height - header - footer) * .8; shift++)
        {
            double error = 0; int count = 0;
            for (int y = header + 4; y < previous.Height - footer - shift - 4; y += 5)
            for (int x = 8; x < previous.Width - 8; x += 13) for (int c = 0; c < 3; c++)
            { error += Math.Abs(previous.At(x, y + shift, c) - next.At(x, y, c)); count++; }
            if (count > 0) candidates.Add((shift, error / count));
        }
        if (candidates.Count == 0) return null;
        var sorted = candidates.OrderBy(p => p.Error).ToArray(); var best = sorted[0];
        double alternative = sorted.Where(p => Math.Abs(p.Shift - best.Shift) > 4).Select(p => p.Error).DefaultIfEmpty(255).Min();
        if (best.Error > 9 || alternative - best.Error < 1.2) return null;
        return new(best.Shift, header, footer, best.Error);
    }
}
