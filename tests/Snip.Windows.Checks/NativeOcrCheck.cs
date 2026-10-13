using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snip.Core;
using Snip.Infrastructure;

namespace Snip.Windows.Checks;

internal static class NativeOcrCheck
{
    public static int Run()
    {
        try
        {
            var service = new OfflineOcrService();
            var languages = Task.Run(() => service.Languages).WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            var english = languages.FirstOrDefault(l => l.StartsWith("en", StringComparison.OrdinalIgnoreCase));
            if (english is null) { Console.WriteLine("UNRUN native OCR: English Windows OCR language component is not installed."); return 2; }
            var bytes = Enumerable.Repeat((byte)255, 600 * 150 * 4).ToArray();
            var background = BitmapSource.Create(600, 150, 96, 96, PixelFormats.Bgra32, null, bytes, 600 * 4); background.Freeze();
            var annotation = new Annotation(Guid.NewGuid(), AnnotationTool.Text, new(20, 25), new(550, 120), "#000000", 2, 48, 8, "Hello world", []);
            var image = new AnnotationRenderer().Render(background, [annotation]);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = new MemoryStream(); encoder.Save(stream);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var result = service.RecognizeAsync(new ImagePayload(stream.ToArray(), 600, 150), english, cancellation.Token).WaitAsync(cancellation.Token).GetAwaiter().GetResult();
            var text = string.Join(" ", result.Regions.Select(r => r.Text));
            if (!text.Contains("Hello", StringComparison.OrdinalIgnoreCase) || !text.Contains("world", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Native OCR semantic assertion failed: " + text);
            Console.WriteLine("PASS native Windows OCR on background worker: " + text); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine("FAIL native OCR: " + e); return 1; }
    }
}
