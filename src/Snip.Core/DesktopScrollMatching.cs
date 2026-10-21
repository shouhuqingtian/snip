namespace Snip.Core;

/// <summary>Match motion within desktop content, excluding unchanged controls at both ends of each correspondence.</summary>
public static class DesktopScrollMatching
{
    public static ScrollMatch? Match(PixelFrame previous, PixelFrame next, CancellationToken token = default, DiagnosticOperation? diagnostics = null)
    {
        token.ThrowIfCancellationRequested();
        if (previous.Width != next.Width || previous.Height != next.Height || previous.Height < 120) return null;
        int width = previous.Width, height = previous.Height;
        bool Changed(int x, int y)
        {
            int error = 0;
            for (int c = 0; c < 3; c++) error += Math.Abs(previous.At(x, y, c) - next.At(x, y, c));
            return error >= 18;
        }
        var anchors = new List<(int X, int Y)>();
        for (int top = 3; top < height - 3; top += 24)
        {
            token.ThrowIfCancellationRequested();
            for (int left = 3; left < width - 3; left += 32)
            {
                int selected = 0;
                for (int y = top; y < Math.Min(top + 24, height - 3) && selected < 4; y += 3)
                for (int x = left; x < Math.Min(left + 32, width - 3) && selected < 4; x += 3)
                {
                    if (!Changed(x, y)) continue;
                    int contrast = 0;
                    for (int c = 0; c < 3; c++) contrast += Math.Abs(previous.At(x, y, c) - previous.At(x + 2, y, c)) + Math.Abs(previous.At(x, y, c) - previous.At(x, y + 2, c));
                    if (contrast >= 45) { anchors.Add((x, y)); selected++; }
                }
            }
        }
        ScrollMatch? Reject(string reason, int candidates = 0, double? error = null, double? gap = null, int? shift = null, int? header = null, int? footer = null)
        {
            diagnostics?.Stage("desktop-overlap-evaluated", new { Reason = reason, Anchors = anchors.Count, Candidates = candidates, Error = error, Gap = gap, Offset = shift, Header = header, Footer = footer });
            return null;
        }
        if (anchors.Count < 12) return Reject("too-few-anchors");
        // Bound matching work while retaining anchors spread throughout the moving content.
        var points = anchors.Count <= 512 ? anchors.ToArray() : Enumerable.Range(0, 512).Select(i => anchors[i * (anchors.Count - 1) / 511]).ToArray();
        var candidates = new List<(int Shift, double Error)>();
        int minimum = Math.Max(12, points.Length / 6), range = (int)(height * .8);
        double Patch(int x, int y, int shift)
        {
            double error = 0;
            for (int c = 0; c < 3; c++)
            {
                error += Math.Abs(previous.At(x, y, c) - next.At(x, y - shift, c));
                error += Math.Abs(previous.At(x + 2, y + 2, c) - next.At(x + 2, y + 2 - shift, c));
                error += Math.Abs(previous.At(x - 2, y - 2, c) - next.At(x - 2, y - 2 - shift, c));
            }
            return error / 9;
        }
        for (int shift = -range; shift <= range; shift++)
        {
            if ((shift & 31) == 0) token.ThrowIfCancellationRequested();
            if (shift == 0) continue;
            double sum = 0; int count = 0;
            foreach (var (x, y) in points)
            {
                int targetY = y - shift;
                // A source anchor moving into an unchanged frozen header/sidebar is not an overlap candidate.
                if (targetY < 3 || targetY >= height - 3 || !Changed(x, targetY)) continue;
                sum += Math.Min(64, Patch(x, y, shift)); count++;
            }
            if (count >= minimum) candidates.Add((shift, sum / count));
        }
        if (candidates.Count == 0) return Reject("insufficient-overlap");
        var ordered = candidates.OrderBy(v => v.Error).ToArray(); var best = ordered[0];
        double alternative = ordered.Where(v => Math.Abs(v.Shift - best.Shift) > 4).Select(v => v.Error).DefaultIfEmpty(64).Min();
        if (best.Error > 12 || alternative - best.Error < 1.2) return Reject("ambiguous-overlap", candidates.Count, best.Error, alternative - best.Error, best.Shift);
        var inliers = points.Where(p => p.Y - best.Shift >= 3 && p.Y - best.Shift < height - 3 && Changed(p.X, p.Y - best.Shift) && Patch(p.X, p.Y, best.Shift) <= 12).ToArray();
        if (inliers.Length < minimum) return Reject("insufficient-inliers", candidates.Count, best.Error, alternative - best.Error, best.Shift);
        bool StaticRow(int y)
        {
            int changed = 0, samples = Math.Max(1, (width - 6) / 5);
            for (int x = 3; x < width - 3; x += 5)
                if (Changed(x, y) && ++changed > Math.Max(1, samples / 200)) return false;
            return true;
        }
        int header = 0, footer = 0;
        // Frozen toolbars/rows can occupy most of a window; the browser's 20% band limit is unsuitable here.
        while (header < height - 32 && StaticRow(header)) header++;
        while (footer < height - header - 32 && StaticRow(height - footer - 1)) footer++;
        header = Math.Max(header, Math.Max(0, inliers.Min(p => Math.Min(p.Y, p.Y - best.Shift)) - 3));
        footer = Math.Max(footer, Math.Max(0, height - inliers.Max(p => Math.Max(p.Y, p.Y - best.Shift)) - 3));
        if (height - header - footer <= Math.Abs(best.Shift)) return Reject("moving-area-too-small", candidates.Count, best.Error, alternative - best.Error, best.Shift, header, footer);
        diagnostics?.Stage("desktop-overlap-evaluated", new { Reason = "accepted", Anchors = anchors.Count, Candidates = candidates.Count, Offset = best.Shift, best.Error, Gap = alternative - best.Error, Header = header, Footer = footer });
        return new(best.Shift, header, footer, best.Error);
    }
}
