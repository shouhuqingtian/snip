using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snip.Core;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using OcrResult = Snip.Core.OcrResult;

namespace Snip.Infrastructure;

public sealed class OfflineOcrService : IOcrService
{
    public IReadOnlyList<string> Languages => OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToArray();
    public Task<OcrResult> RecognizeAsync(ImagePayload image, CancellationToken token) => RecognizeAsync(image, null, token);
    public Task<OcrResult> RecognizeAsync(ImagePayload payload, string? language, CancellationToken token)
        => Task.Run(() => RecognizeCoreAsync(payload, language, token), token);

    private static async Task<OcrResult> RecognizeCoreAsync(ImagePayload payload, string? language, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var input = new MemoryStream(payload.PngBytes.ToArray());
        var image = System.Windows.Media.Imaging.BitmapFrame.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad); image.Freeze();
        if (image.PixelWidth != payload.Width || image.PixelHeight != payload.Height) throw new InvalidDataException("OCR 图片尺寸与声明不一致。");
        var engine = string.IsNullOrEmpty(language) ? OcrEngine.TryCreateFromUserProfileLanguages() : OcrEngine.TryCreateFromLanguage(new Language(language));
        if (engine is null) throw new InvalidOperationException("未安装所选 Windows OCR 语言组件。请在 Windows 设置→语言和区域中添加中文/英语及其文字识别组件，然后重试。");
        var source = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0); source.Freeze();
        int limit = Math.Min(1800, (int)OcrEngine.MaxImageDimension);
        var results = new List<OcrTextRegion>();
        // Preserve source pixels; overlap tiles and retain lines by their center to avoid duplicates.
        int step = limit - 120;
        for (int y = 0; y < source.PixelHeight; y += step) for (int x = 0; x < source.PixelWidth; x += step)
        {
            token.ThrowIfCancellationRequested();
            int width = Math.Min(limit, source.PixelWidth - x), height = Math.Min(limit, source.PixelHeight - y);
            var bytes = new byte[width * height * 4]; source.CopyPixels(new System.Windows.Int32Rect(x, y, width, height), bytes, width * 4, 0);
            using var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Ignore);
            bitmap.CopyFromBuffer(bytes.AsBuffer());
            var result = await engine.RecognizeAsync(bitmap).AsTask(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            foreach (var line in result.Lines)
            {
                if (line.Words.Count == 0) continue;
                double left = line.Words.Min(w => w.BoundingRect.X), top = line.Words.Min(w => w.BoundingRect.Y);
                double centerX = (left + line.Words.Max(w => w.BoundingRect.Right)) / 2;
                double centerY = (top + line.Words.Max(w => w.BoundingRect.Bottom)) / 2;
                if (x > 0 && centerX < 60 || y > 0 && centerY < 60) continue;
                if (x + width < source.PixelWidth && centerX >= width - 60 || y + height < source.PixelHeight && centerY >= height - 60) continue;
                double right = line.Words.Max(w => w.BoundingRect.Right), bottom = line.Words.Max(w => w.BoundingRect.Bottom);
                results.Add(new(line.Text, new PixelRect(x + left, y + top, right - left, bottom - top), null));
            }
        }
        return new(results.OrderBy(r => Math.Round(r.Bounds.Y / 12)).ThenBy(r => r.Bounds.X).ToArray());
    }
}
