namespace Snip.Core;

/// <summary>Match motion within desktop content, excluding unchanged controls at both ends of each correspondence.</summary>
public static class DesktopScrollMatching
{
    private static readonly Lazy<bool> prepared = new(() =>
    {
        var bytes = new byte[128 * 224 * 4]; new Random(55).NextBytes(bytes);
        var a = new PixelFrame(128, 200, bytes.AsSpan(0, 128 * 200 * 4).ToArray());
        var b = new PixelFrame(128, 200, bytes.AsSpan(128 * 24 * 4, 128 * 200 * 4).ToArray());
        var match = Match(a, b);
        if (match is not null) { using var frames = new ScrollFrameAccumulator(a); frames.Append(b, match); }
        _ = AlignedBody(a, a, 0, 0);
        return true;
    });
    // Called on the capture worker before announcing readiness. First-wheel input must
    // not pay the JIT/collection initialization cost and lose intermediate overlap.
    public static void Prepare(CancellationToken token = default)
    { token.ThrowIfCancellationRequested(); _ = prepared.Value; token.ThrowIfCancellationRequested(); }

    public static ScrollMatch? Match(PixelFrame previous, PixelFrame next, CancellationToken token = default, DiagnosticOperation? diagnostics = null, ScrollMatch? reference = null)
    {
        token.ThrowIfCancellationRequested();
        if (previous.Width != next.Width || previous.Height != next.Height || previous.Height < 120) return null;
        int width = previous.Width, height = previous.Height;
        var changed = new byte[width][];
        bool Changed(int x, int y)
        {
            var column = changed[x] ??= new byte[height];
            if (column[y] != 0) return column[y] == 2;
            int error = 0;
            for (int c = 0; c < 3; c++) error += Math.Abs(previous.At(x, y, c) - next.At(x, y, c));
            column[y] = error >= 18 ? (byte)2 : (byte)1;
            return error >= 18;
        }
        bool StaticRow(int y)
        {
            int changedPixels = 0, samples = Math.Max(1, (width - 6) / 5);
            for (int x = 3; x < width - 3; x += 5)
                if (Changed(x, y) && ++changedPixels > Math.Max(1, samples / 200)) return false;
            return true;
        }
        int header = reference?.Header ?? 0, footer = reference?.Footer ?? 0;
        if (reference is null)
        {
            while (header < height - 32 && StaticRow(header)) header++;
            while (footer < height - header - 32 && StaticRow(height - footer - 1)) footer++;
        }
        var anchors = new List<(int X, int Y)>();
        for (int top = header + 5; top < height - footer - 5; top += 24)
        {
            token.ThrowIfCancellationRequested();
            for (int left = 5; left < width - 5; left += 32)
            {
                int selected = 0;
                for (int y = top; y < Math.Min(top + 24, height - footer - 5) && selected < 4; y += 2)
                for (int x = left; x < Math.Min(left + 32, width - 5) && selected < 4; x += 2)
                {
                    if (!Changed(x, y)) continue;
                    if (Textured(previous, x, y)) { anchors.Add((x, y)); selected++; }
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
        int range = (int)(height * .8);
        var votes = new Dictionary<int, int>(); var distinct = new List<(int X, int Y)>();
        // Index patches once per sampled column. Repeated grid intersections cannot
        // outvote distinctive glyphs/row numbers, or dominate the candidate score.
        foreach (var column in points.GroupBy(p => p.X))
        {
            token.ThrowIfCancellationRequested();
            var keys = column.Select(p => Signature(previous, p.X, p.Y)).Distinct().ToDictionary(key => key, _ => new List<int>());
            for (int y = header + 5; y < height - footer - 5; y++)
            {
                ulong key = Signature(next, column.Key, y);
                if (keys.TryGetValue(key, out var hits) && hits.Count <= 8) hits.Add(y);
            }
            foreach (var point in column)
            {
                var hits = keys[Signature(previous, point.X, point.Y)];
                if (hits.Count > 8) continue;
                // Keep unmatched anchors in verification. Otherwise only shared glyph
                // fragments survive and unrelated rows can appear perfectly aligned.
                distinct.Add(point);
                foreach (int y in hits)
                {
                    int shift = point.Y - y;
                    if (shift != 0 && Math.Abs(shift) <= range) votes[shift] = votes.GetValueOrDefault(shift) + 1;
                }
            }
        }
        points = distinct.ToArray();
        if (points.Length < 12) return Reject("too-few-distinct-anchors");
        var candidates = new List<(int Shift, double Error)>();
        int minimum = Math.Max(12, points.Length / 6);
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
        var shifts = votes.Where(p => p.Value >= 3).OrderByDescending(p => p.Value).Take(32)
            .SelectMany(p => Enumerable.Range(p.Key - 2, 5)).Where(shift => shift != 0 && Math.Abs(shift) <= range).Distinct();
        foreach (int shift in shifts)
        {
            token.ThrowIfCancellationRequested();
            double sum = 0; int count = 0;
            foreach (var (x, y) in points)
            {
                int targetY = y - shift;
                if (targetY < header + 5 || targetY >= height - footer - 5) continue;
                sum += Math.Min(64, Patch(x, y, shift)); count++;
            }
            if (count >= minimum) candidates.Add((shift, sum / count));
        }
        if (candidates.Count == 0) return Reject("insufficient-overlap");
        var ordered = candidates.OrderBy(v => v.Error).ToArray(); var best = ordered[0];
        double alternative = ordered.Where(v => Math.Abs(v.Shift - best.Shift) > 4).Select(v => v.Error).DefaultIfEmpty(64).Min();
        if (best.Error > 12 || alternative - best.Error < 1.2) return Reject("ambiguous-overlap", candidates.Count, best.Error, alternative - best.Error, best.Shift);
        var inliers = points.Where(p => p.Y - best.Shift >= header + 5 && p.Y - best.Shift < height - footer - 5 && Patch(p.X, p.Y, best.Shift) <= 12).ToArray();
        if (inliers.Length < minimum) return Reject("insufficient-inliers", candidates.Count, best.Error, alternative - best.Error, best.Shift);
        if (height - header - footer <= Math.Abs(best.Shift)) return Reject("moving-area-too-small", candidates.Count, best.Error, alternative - best.Error, best.Shift, header, footer);
        diagnostics?.Stage("desktop-overlap-evaluated", new { Reason = "accepted", Anchors = anchors.Count, Candidates = candidates.Count, Offset = best.Shift, best.Error, Gap = alternative - best.Error, Header = header, Footer = footer });
        return new(best.Shift, header, footer, best.Error);
    }

    /// <summary>Confirm saved body alignment without requiring fixed controls/carets to be identical.</summary>
    public static bool AlignedBody(PixelFrame previous, PixelFrame next, int header, int footer, CancellationToken token = default)
    {
        if (previous.Width != next.Width || previous.Height != next.Height || header < 0 || footer < 0) return false;
        int count = 0, different = 0;
        for (int top = header + 5; top < previous.Height - footer - 5; top += 24)
        {
            token.ThrowIfCancellationRequested();
            for (int left = 5; left < previous.Width - 5; left += 32)
            {
                int selected = 0;
                for (int y = top; y < Math.Min(top + 24, previous.Height - footer - 5) && selected < 4; y += 2)
                for (int x = left; x < Math.Min(left + 32, previous.Width - 5) && selected < 4; x += 2)
                {
                    if (!Textured(previous, x, y)) continue;
                    selected++; count++;
                    if (Signature(previous, x, y) != Signature(next, x, y)) different++;
                }
            }
        }
        return count >= 16 && different <= Math.Max(1, count / 100);
    }
    private static bool Textured(PixelFrame frame, int x, int y)
    {
        int horizontal = 0, vertical = 0;
        for (int c = 0; c < 3; c++)
        {
            horizontal += Math.Abs(frame.At(x, y, c) - frame.At(x + 2, y, c));
            vertical += Math.Abs(frame.At(x, y, c) - frame.At(x, y + 2, c));
        }
        return horizontal >= 18 && vertical >= 18 && horizontal + vertical >= 45;
    }
    private static ulong Signature(PixelFrame frame, int x, int y)
    {
        ulong hash = 0;
        Add(x, y); Add(x + 2, y); Add(x - 2, y); Add(x, y + 2); Add(x, y - 2);
        Add(x + 5, y); Add(x - 5, y); Add(x, y + 5); Add(x, y - 5);
        Add(x + 3, y + 3); Add(x - 3, y - 3); Add(x + 5, y + 5); Add(x - 5, y - 5);
        return hash;
        void Add(int px, int py)
        {
            int index = (py * frame.Width + px) * 4;
            uint luminance = (uint)(frame.Bgra[index] * 29 + frame.Bgra[index + 1] * 150 + frame.Bgra[index + 2] * 77) >> 12;
            hash = (hash << 4) | luminance;
        }
    }
}
