using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snip.Core;

namespace Snip.Infrastructure;

/// <summary>Shared preview/export renderer. One drawing unit equals one output pixel.</summary>
public sealed class AnnotationRenderer
{
    private static Point Point(PixelPoint p) => new(p.X, p.Y);
    public static Rect Rect(PixelRect r) => new(r.X, r.Y, r.Width, r.Height);
    private static Brush ColorBrush(string color) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));

    public BitmapSource Render(BitmapSource image, IEnumerable<Annotation> annotations)
    {
        var layers = annotations as IReadOnlyList<Annotation> ?? annotations.ToArray();
        if (layers.Count == 0)
        {
            if (image.IsFrozen) return image;
            var copy = image.Clone(); copy.Freeze(); return copy;
        }
        return Rasterize(BuildDrawing(image, layers), image.PixelWidth, image.PixelHeight);
    }

    /// <summary>Keep ordinary annotations as vectors; rasterize only mosaic patches.</summary>
    public DrawingGroup BuildDrawing(BitmapSource image, IEnumerable<Annotation> annotations)
    {
        var group = new DrawingGroup();
        group.Children.Add(new ImageDrawing(image, new Rect(0, 0, image.PixelWidth, image.PixelHeight)));
        foreach (var annotation in annotations)
        {
            if (annotation.Tool == AnnotationTool.Mosaic)
            {
                var rect = annotation.Bounds.RoundOut().Intersect(new(0, 0, image.PixelWidth, image.PixelHeight));
                if (rect.IsEmpty) continue;
                var prior = Rasterize(group, (int)rect.Width, (int)rect.Height, (int)rect.X, (int)rect.Y);
                var local = new PixelRect(0, 0, rect.Width, rect.Height);
                group.Children.Add(new ImageDrawing(Pixelate(prior, local, annotation.MosaicSize), Rect(rect)));
            }
            else
            {
                var layer = new DrawingGroup();
                using (var context = layer.Open()) DrawAnnotation(context, annotation);
                group.Children.Add(layer);
            }
        }
        group.Freeze();
        return group;
    }

    private static BitmapSource Rasterize(Drawing drawing, int width, int height, int originX = 0, int originY = 0)
    {
        if (width > 4096 || height > 4096)
        {
            var output = new WriteableBitmap(width, height, 96, 96, PixelFormats.Pbgra32, null);
            for (int y = 0; y < height; y += 2048) for (int x = 0; x < width; x += 2048)
            {
                int w = Math.Min(2048, width - x), h = Math.Min(2048, height - y);
                var tile = new DrawingVisual();
                using (var dc = tile.RenderOpen()) { dc.PushTransform(new TranslateTransform(-x - originX, -y - originY)); dc.DrawDrawing(drawing); dc.Pop(); }
                var targetTile = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32); targetTile.Render(tile);
                var buffer = new byte[w * h * 4]; targetTile.CopyPixels(buffer, w * 4, 0);
                output.WritePixels(new Int32Rect(x, y, w, h), buffer, w * 4, 0);
            }
            output.Freeze(); return output;
        }
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.PushTransform(new TranslateTransform(-originX, -originY));
            context.DrawDrawing(drawing); context.Pop();
        }
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    private static BitmapSource Pixelate(BitmapSource source, PixelRect bounds, int size)
    {
        int width = (int)bounds.Width, height = (int)bounds.Height, stride = checked(width * 4);
        var bytes = new byte[checked(stride * height)];
        source.CopyPixels(new Int32Rect((int)bounds.X, (int)bounds.Y, width, height), bytes, stride, 0);
        size = Math.Clamp(size, 4, 64);
        for (int y = 0; y < height; y += size)
        for (int x = 0; x < width; x += size)
        {
            int endY = Math.Min(y + size, height), endX = Math.Min(x + size, width), count = (endY - y) * (endX - x);
            long blue = 0, green = 0, red = 0;
            for (int row = y; row < endY; row++)
            for (int col = x; col < endX; col++)
            {
                int i = row * stride + col * 4;
                blue += bytes[i]; green += bytes[i + 1]; red += bytes[i + 2];
            }
            for (int row = y; row < endY; row++)
            for (int col = x; col < endX; col++)
            {
                int i = row * stride + col * 4;
                bytes[i] = (byte)(blue / count); bytes[i + 1] = (byte)(green / count); bytes[i + 2] = (byte)(red / count); bytes[i + 3] = 255;
            }
        }
        var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, bytes, stride);
        result.Freeze();
        return result;
    }

    public void DrawAnnotation(DrawingContext context, Annotation annotation)
    {
        var brush = ColorBrush(annotation.Color);
        var pen = new System.Windows.Media.Pen(brush, annotation.StrokeWidth) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        switch (annotation.Tool)
        {
            case AnnotationTool.Rectangle: context.DrawRectangle(annotation.Filled ? brush : null, pen, Rect(annotation.Bounds)); break;
            case AnnotationTool.Ellipse:
                var r = annotation.Bounds;
                context.DrawEllipse(annotation.Filled ? brush : null, pen, new Point(r.X + r.Width / 2, r.Y + r.Height / 2), r.Width / 2, r.Height / 2);
                break;
            case AnnotationTool.Arrow:
                context.DrawLine(pen, Point(annotation.Start), Point(annotation.End));
                var angle = Math.Atan2(annotation.End.Y - annotation.Start.Y, annotation.End.X - annotation.Start.X);
                var length = Math.Max(12, annotation.StrokeWidth * 4);
                context.DrawLine(pen, Point(annotation.End), new Point(annotation.End.X - length * Math.Cos(angle - Math.PI / 6), annotation.End.Y - length * Math.Sin(angle - Math.PI / 6)));
                context.DrawLine(pen, Point(annotation.End), new Point(annotation.End.X - length * Math.Cos(angle + Math.PI / 6), annotation.End.Y - length * Math.Sin(angle + Math.PI / 6)));
                break;
            case AnnotationTool.Pen:
                if (annotation.Points.Count == 0) break;
                if (annotation.Points.Count == 1) context.DrawEllipse(brush, null, Point(annotation.Points[0]), annotation.StrokeWidth / 2, annotation.StrokeWidth / 2);
                else
                {
                    var geometry = new StreamGeometry();
                    using (var path = geometry.Open())
                    {
                        path.BeginFigure(Point(annotation.Points[0]), false, false);
                        path.PolyLineTo(annotation.Points.Skip(1).Select(Point).ToArray(), true, false);
                    }
                    context.DrawGeometry(null, pen, geometry);
                }
                break;
            case AnnotationTool.Text:
                context.DrawText(FormatText(annotation), Point(annotation.Start));
                break;
            case AnnotationTool.Mosaic:
                context.DrawRectangle(new SolidColorBrush(Color.FromArgb(100, 80, 80, 80)), new System.Windows.Media.Pen(Brushes.White, 1), Rect(annotation.Bounds));
                break;
            case AnnotationTool.Sticker:
                if (!annotation.StickerPng.IsEmpty)
                {
                    using var stream = new MemoryStream(annotation.StickerPng.ToArray());
                    var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze();
                    context.DrawImage(image, Rect(annotation.Bounds));
                }
                break;
        }
    }

    private static FormattedText FormatText(Annotation a) => new(a.Text, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
        new Typeface("Microsoft YaHei UI"), a.FontSize, ColorBrush(a.Color), 1);

    public PixelRect GetBounds(Annotation a)
    {
        if (a.Tool != AnnotationTool.Text) return a.Bounds;
        var text = FormatText(a);
        return new(a.Start.X, a.Start.Y, text.WidthIncludingTrailingWhitespace, text.Height);
    }

    public bool HitTest(Annotation a, PixelPoint p, double tolerance)
    {
        var r = GetBounds(a);
        if (a.Tool is AnnotationTool.Text or AnnotationTool.Mosaic or AnnotationTool.Sticker || (a.Filled && a.Tool is AnnotationTool.Rectangle or AnnotationTool.Ellipse)) return r.Contains(p);
        var limit = tolerance + a.StrokeWidth / 2;
        if (a.Tool == AnnotationTool.Arrow) return SegmentDistance(p, a.Start, a.End) <= limit;
        if (a.Tool == AnnotationTool.Pen)
        {
            if (a.Points.Count == 1) return SegmentDistance(p, a.Points[0], a.Points[0]) <= limit;
            return a.Points.Zip(a.Points.Skip(1), (s, e) => SegmentDistance(p, s, e)).Any(d => d <= limit);
        }
        if (a.Tool == AnnotationTool.Ellipse && r.Width > 0 && r.Height > 0)
        {
            double dx = (p.X - r.X - r.Width / 2) / (r.Width / 2), dy = (p.Y - r.Y - r.Height / 2) / (r.Height / 2);
            return Math.Abs(Math.Sqrt(dx * dx + dy * dy) - 1) * Math.Min(r.Width, r.Height) / 2 <= limit;
        }
        var expanded = new PixelRect(r.X - limit, r.Y - limit, r.Width + limit * 2, r.Height + limit * 2);
        var inside = new PixelRect(r.X + limit, r.Y + limit, Math.Max(0, r.Width - limit * 2), Math.Max(0, r.Height - limit * 2));
        return expanded.Contains(p) && (inside.IsEmpty || !inside.Contains(p));
    }

    private static double SegmentDistance(PixelPoint p, PixelPoint a, PixelPoint b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, length = dx * dx + dy * dy;
        double t = length == 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / length, 0, 1);
        return Math.Sqrt(Math.Pow(p.X - a.X - t * dx, 2) + Math.Pow(p.Y - a.Y - t * dy, 2));
    }
}
