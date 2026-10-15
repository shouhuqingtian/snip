using System.Text;

namespace Snip.Core;

/// <summary>Horizontal screenshot text: group overlapping baselines before ordering left to right.</summary>
public static class OcrTextLayout
{
    public static OcrResult Arrange(IReadOnlyList<OcrTextRegion> regions)
    {
        if (regions.Count == 0) return new([], "");
        var rows = new List<Row>();
        foreach (var region in regions.OrderBy(r => Center(r.Bounds)).ThenBy(r => r.Bounds.X))
        {
            // Compare fixed anchors, not a growing union: otherwise nearby rows can bridge together.
            Row? best = null;
            double distance = double.PositiveInfinity;
            for (int i = rows.Count - 1; i >= 0; i--)
            {
                var row = rows[i];
                double delta = Center(region.Bounds) - Center(row.Anchor);
                if (delta > region.Bounds.Height && delta > row.Anchor.Height) break;
                double overlap = Math.Min(region.Bounds.Bottom, row.Anchor.Bottom) - Math.Max(region.Bounds.Y, row.Anchor.Y);
                double shorter = Math.Min(region.Bounds.Height, row.Anchor.Height);
                if (overlap < shorter * 0.55 || Math.Abs(delta) > shorter * 0.45 || Math.Abs(delta) >= distance) continue;
                best = row; distance = Math.Abs(delta);
            }
            if (best is null) rows.Add(new(region));
            else best.Regions.Add(region);
        }
        var ordered = new List<OcrTextRegion>(regions.Count);
        var text = new StringBuilder();
        double left = regions.Min(r => r.Bounds.X);
        double lineHeight = Median(regions.Select(r => r.Bounds.Height));
        double cellWidth = Math.Clamp(Median(regions.Where(r => Cells(r.Text) > 0).Select(r => r.Bounds.Width / Cells(r.Text))), lineHeight * 0.25, lineHeight * 0.75);
        double? previousBottom = null;
        foreach (var row in rows.OrderBy(r => r.Regions.Min(region => region.Bounds.Y)).ThenBy(r => r.Anchor.X))
        {
            double top = row.Regions.Min(r => r.Bounds.Y);
            if (previousBottom is { } bottom)
            {
                text.AppendLine();
                int blanks = Math.Clamp((int)Math.Floor((top - bottom) / lineHeight), 0, 3);
                for (int i = 0; i < blanks; i++) text.AppendLine();
            }
            int column = 0;
            OcrTextRegion? previous = null;
            foreach (var region in row.Regions.OrderBy(r => r.Bounds.X))
            {
                int minimumGap = 0;
                if (previous is not null)
                {
                    double gap = region.Bounds.X - previous.Bounds.Right;
                    double height = Math.Min(region.Bounds.Height, previous.Bounds.Height);
                    if (gap > height * 0.2) minimumGap = 1;
                }
                int target = (int)Math.Round((region.Bounds.X - left) / cellWidth);
                int spaces = Math.Clamp(target - column, minimumGap, 2048);
                text.Append(' ', spaces); column += spaces;
                text.Append(region.Text);
                column += Cells(region.Text);
                ordered.Add(region); previous = region;
            }
            previousBottom = row.Regions.Max(r => r.Bounds.Bottom);
        }
        return new(ordered, text.ToString());
    }
    // A compact plain-text grid: CJK/full-width characters take two columns; combining marks take none.
    private static int Cells(string text)
    {
        int count = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.EnclosingMark or System.Globalization.UnicodeCategory.Format) continue;
            int value = rune.Value;
            count += value is >= 0x2e80 and <= 0xa4cf or >= 0xac00 and <= 0xd7af or >= 0xf900 and <= 0xfaff or >= 0xff01 and <= 0xff60 or >= 0xffe0 and <= 0xffe6 or >= 0x20000 and <= 0x3ffff ? 2 : 1;
        }
        return count;
    }
    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? 1 : sorted[sorted.Length / 2];
    }
    private static double Center(PixelRect bounds) => bounds.Y + bounds.Height / 2;
    private sealed class Row(OcrTextRegion first)
    {
        public PixelRect Anchor { get; } = first.Bounds;
        public List<OcrTextRegion> Regions { get; } = [first];
    }
}
