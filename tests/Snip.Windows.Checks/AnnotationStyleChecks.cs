using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snip.Core;
using Snip.Infrastructure;

namespace Snip.Windows.Checks;

internal static class AnnotationStyleChecks
{
    internal static int Run()
    {
        try
        {
            var renderer = new AnnotationRenderer();
            var blank = White(1200, 760);
            var shapes = new List<Annotation>();
            foreach (double width in new[] { 2d, 4, 6, 8 })
            {
                double y = 105 + shapes.Count / 3 * 145;
                shapes.Add(Shape(AnnotationTool.Rectangle, new(55, y), new(330, y + 92), width));
                shapes.Add(Shape(AnnotationTool.Ellipse, new(410, y), new(690, y + 92), width));
                shapes.Add(Shape(AnnotationTool.Arrow, new(770, y + 78), new(1115, y + 8), width));
            }
            var rendered = renderer.Render(blank, shapes);
            // The transient pointer preview and committed/export drawing must share exact pixels.
            var preview = new DrawingVisual();
            using (var dc = preview.RenderOpen())
            {
                dc.DrawImage(blank, new Rect(0, 0, blank.PixelWidth, blank.PixelHeight));
                foreach (var a in shapes) renderer.DrawAnnotation(dc, a);
            }
            var direct = new RenderTargetBitmap(1200, 760, 96, 96, PixelFormats.Pbgra32); direct.Render(preview); direct.Freeze();
            Require(Bytes(direct).SequenceEqual(Bytes(rendered)), "Pointer preview and exported shape pixels differ.");
            Require(Pixel(rendered, 160, 150).Take(3).All(v => v == 255), "Hollow rectangle lost its interior.");
            Require(Bytes(rendered).Where((_, i) => i % 4 == 1).Any(v => v is > 60 and < 240), "Shape boundaries lost antialias coverage.");
            Console.WriteLine("PASS antialiased rectangle/ellipse/arrow at line widths 2/4/6/8; source-resolution preview and committed/export pixels match exactly.");
            foreach (double angle in new[] { 0d, .25, .8, 1.57, 2.2, 3.14, 4.2, 5.3 })
            foreach (double distance in new[] { .1, 3, 12, 35, 200 })
            {
                var start = new PixelPoint(300, 300);
                var end = new PixelPoint(start.X + distance * Math.Cos(angle), start.Y + distance * Math.Sin(angle));
                var a = Shape(AnnotationTool.Arrow, start, end, 6);
                var bounds = renderer.GetBounds(a);
                Require(double.IsFinite(bounds.Width) && bounds.Width > 0 && bounds.Height > 0, "Short/reversed arrow has invalid bounds.");
                Require(renderer.HitTest(a, new((start.X + end.X) / 2, (start.Y + end.Y) / 2), 2), "Visible arrow cannot be selected.");
                _ = renderer.Render(White(600, 600), [a]);
            }
            var horizontal = Shape(AnnotationTool.Arrow, new(100, 100), new(250, 100), 6);
            Require(renderer.HitTest(horizontal, new(235, 106), .25), "Filled arrowhead cannot be selected away from the shaft.");
            Require(!renderer.HitTest(horizontal, new(120, 120), 1), "Arrow hit test selects empty surrounding pixels.");
            Console.WriteLine("PASS short, zero-length and reversed arrow geometry; actual head/shaft hit tests include the whole visible silhouette.");
            var path = "artifacts/validation/ui-previews/51-annotation-styles.png";
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var gallery = new DrawingVisual();
            using (var dc = gallery.RenderOpen())
            {
                dc.DrawImage(rendered, new Rect(0, 0, 1200, 760));
                void Text(string value, double x, double y, double size) => dc.DrawText(new FormattedText(value, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, Brushes.DimGray, 1), new Point(x, y));
                Text("SelfSnip · Annotation styles", 55, 28, 28);
                Text("Rectangle", 55, 73, 16); Text("Ellipse", 410, 73, 16); Text("Arrow", 770, 73, 16);
                for (int i = 0; i < 4; i++) Text("Line width " + new[] { 2, 4, 6, 8 }[i], 55, 205 + i * 145, 13);
                Text("Shared vector preview / PNG export · rounded box corners · proportional filled arrowheads", 55, 715, 16);
            }
            var result = new RenderTargetBitmap(1200, 760, 96, 96, PixelFormats.Pbgra32); result.Render(gallery); result.Freeze();
            new ImageExportService().SavePng(result, path);
            Console.WriteLine("PASS public annotation gallery saved for original-resolution visual review.");
            return 0;
        }
        catch (Exception error) { Console.WriteLine("::error title=Annotation style::" + error.GetType().Name + ": " + error.Message); return 1; }
    }
    private static Annotation Shape(AnnotationTool tool, PixelPoint a, PixelPoint b, double width) => new(Guid.NewGuid(), tool, a, b, "#FF3B30", width, 24, 12, "", []);
    private static BitmapSource White(int w, int h)
    {
        var bytes = Enumerable.Repeat((byte)255, w * h * 4).ToArray();
        var image = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, bytes, w * 4); image.Freeze(); return image;
    }
    private static byte[] Bytes(BitmapSource source) { var bytes = new byte[source.PixelWidth * source.PixelHeight * 4]; source.CopyPixels(bytes, source.PixelWidth * 4, 0); return bytes; }
    private static byte[] Pixel(BitmapSource image, int x, int y) { var bytes = new byte[4]; image.CopyPixels(new Int32Rect(x, y, 1, 1), bytes, 4, 0); return bytes; }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
