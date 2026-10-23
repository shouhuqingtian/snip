using System.Globalization;
using System.Text;

namespace Snip.Core;

public sealed record ImageTextGlyph(int Start, int Length, PixelRect Bounds, int WordStart, int WordLength);

/// <summary>Selectable OCR geometry in original pixels; copied text keeps the source row/column layout.</summary>
public sealed class ImageTextDocument
{
    private ImageTextDocument(string text, IReadOnlyList<ImageTextGlyph> glyphs) { Text = text; Glyphs = glyphs; }
    public string Text { get; }
    public IReadOnlyList<ImageTextGlyph> Glyphs { get; }
    public static ImageTextDocument Create(OcrResult result, int width, int height, IReadOnlyList<PixelRect>? redactions = null)
    {
        var image = new PixelRect(0, 0, width, height);
        if (result.Regions.Count > 5000 || result.Regions.Sum(r => (long)(r.Text?.Length ?? 0)) > 100_000)
            throw new InvalidOperationException("图片文字过多，请分段截取后选字。");
        var regions = result.Regions.Where(r => !string.IsNullOrWhiteSpace(r.Text) && Valid(r.Bounds))
            .Select(r => r with { Bounds = r.Bounds.Intersect(image) }).Where(r => !r.Bounds.IsEmpty).ToArray();
        if (redactions is { Count: > 0 })
            regions = regions.SelectMany(region =>
            {
                bool Covered(PixelRect bounds) => redactions.Any(mask => !bounds.Intersect(mask).IsEmpty);
                if (!Covered(region.Bounds)) return new[] { region };
                // Native word boxes preserve unaffected words; line-only engines exclude the whole
                // intersecting block. Never retain redacted source text in a selectable layer.
                return (region.Words ?? []).Where(word => Valid(word.Bounds) && !Covered(word.Bounds))
                    .Select(word => new OcrTextRegion(word.Text, word.Bounds.Intersect(image), region.Confidence, [word]));
            }).ToArray();
        var arranged = OcrTextLayout.Arrange(regions); string text = arranged.LayoutText ?? "";
        if (text.Length > 500_000) throw new InvalidOperationException("图片文字排布过大，请分段截取后选字。");
        var glyphs = new List<ImageTextGlyph>(); int offset = 0;
        foreach (var region in arranged.Regions)
        {
            int start = text.IndexOf(region.Text, offset, StringComparison.Ordinal);
            if (start < 0) continue;
            offset = start + region.Text.Length;
            var mapped = Split(region.Text, region.Bounds, start, start, region.Text.Length).ToDictionary(g => g.Start);
            int wordOffset = 0;
            foreach (var word in region.Words ?? [])
            {
                if (string.IsNullOrEmpty(word.Text) || !Valid(word.Bounds)) continue;
                int index = region.Text.IndexOf(word.Text, wordOffset, StringComparison.Ordinal);
                if (index < 0) continue;
                wordOffset = index + word.Text.Length;
                var bounds = word.Bounds.Intersect(region.Bounds);
                if (bounds.IsEmpty) continue;
                foreach (var glyph in Split(word.Text, bounds, start + index, start + index, word.Text.Length))
                    if (mapped.TryGetValue(glyph.Start, out var original) && original.Length == glyph.Length) mapped[glyph.Start] = glyph;
            }
            glyphs.AddRange(mapped.Values.OrderBy(g => g.Start));
        }
        return new(text, glyphs.AsReadOnly());
    }
    private static bool Valid(PixelRect r) => double.IsFinite(r.X) && double.IsFinite(r.Y) && double.IsFinite(r.Width) && double.IsFinite(r.Height) && !r.IsEmpty;
    private static IEnumerable<ImageTextGlyph> Split(string text, PixelRect bounds, int start, int wordStart, int wordLength)
    {
        int[] indices = StringInfo.ParseCombiningCharacters(text);
        var elements = indices.Select((index, i) => text.Substring(index, (i + 1 < indices.Length ? indices[i + 1] : text.Length) - index)).ToArray();
        double[] weights = elements.Select(Weight).ToArray(); double total = weights.Sum(), x = bounds.X;
        for (int i = 0; i < indices.Length; i++)
        {
            double width = bounds.Width * weights[i] / total;
            yield return new(start + indices[i], elements[i].Length, new(x, bounds.Y, width, bounds.Height), wordStart, wordLength); x += width;
        }
    }
    private static double Weight(string value)
    {
        int rune = Rune.GetRuneAt(value, 0).Value;
        if (rune >= 0x2e80) return 1.6;
        if (char.IsWhiteSpace(value[0])) return 0.5;
        return "ilI!.,:;'|".Contains(value[0]) ? 0.4 : "WM@".Contains(value[0]) ? 1.3 : 0.8;
    }
    public ImageTextGlyph? Hit(PixelPoint point, bool nearest = false)
    {
        ImageTextGlyph? best = null; double distance = double.PositiveInfinity;
        foreach (var glyph in Glyphs)
        {
            var r = glyph.Bounds;
            double dx = Math.Max(Math.Max(r.X - point.X, point.X - r.Right), 0);
            double dy = Math.Max(Math.Max(r.Y - point.Y, point.Y - r.Bottom), 0);
            if (!nearest && (dy > 0 || dx > r.Height * 0.4)) continue;
            double score = dy * dy * 1000 + dx * dx;
            if (score >= distance) continue;
            best = glyph; distance = score;
        }
        return best;
    }
    public IReadOnlyList<PixelRect> Highlights(int start, int end)
    {
        var result = new List<PixelRect>();
        foreach (var glyph in Glyphs)
        {
            if (glyph.Start >= end || glyph.Start + glyph.Length <= start) continue;
            var r = glyph.Bounds;
            if (result.Count > 0 && result[^1] is { } previous && Math.Abs(previous.Y - r.Y) < Math.Min(previous.Height, r.Height) * 0.2 && r.X >= previous.X && r.X <= previous.Right + 2)
                result[^1] = new(previous.X, Math.Min(previous.Y, r.Y), Math.Max(previous.Right, r.Right) - previous.X, Math.Max(previous.Bottom, r.Bottom) - Math.Min(previous.Y, r.Y));
            else result.Add(r);
        }
        return result;
    }
}
