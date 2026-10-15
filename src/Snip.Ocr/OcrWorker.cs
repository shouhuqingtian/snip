using System.Buffers.Binary;
using System.Text.Json;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using RapidOcrNet;
using SkiaSharp;
using Snip.Core;

namespace Snip.Ocr;

public sealed record OcrWorkerReply(Snip.Core.OcrResult Result, long PeakWorkingSetBytes);

/// <summary>Invoked only in the short-lived worker. No native session is constructed in the app.</summary>
public static class OcrWorker
{
    public const int MaxPngBytes = 64 * 1024 * 1024;
    public const long MaxPixels = 64_000_000;
    public static async Task<int> RunAsync(string modelDirectory, Stream input, Stream output)
    {
        try
        {
            if (OperatingSystem.IsWindows()) SetErrorMode(0x0001 | 0x0002);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            var token = deadline.Token;
            byte[] header = new byte[12]; await input.ReadExactlyAsync(header, token).ConfigureAwait(false);
            int width = BinaryPrimitives.ReadInt32LittleEndian(header), height = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
            int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
            if (width <= 0 || height <= 0 || (long)width * height > MaxPixels || length <= 0 || length > MaxPngBytes) return 3;
            byte[] png = new byte[length]; await input.ReadExactlyAsync(png, token).ConfigureAwait(false);
            var models = new OcrModelStore(modelDirectory);
            if (!await models.ValidateAsync(token).ConfigureAwait(false)) return 4;
            using var image = SKBitmap.Decode(png);
            if (image is null || image.Width != width || image.Height != height) return 3;
            var result = Recognize(image, modelDirectory, token);
            using var process = Process.GetCurrentProcess(); process.Refresh();
            await JsonSerializer.SerializeAsync(output, new OcrWorkerReply(result, process.PeakWorkingSet64), cancellationToken: token).ConfigureAwait(false);
            await output.FlushAsync(token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) { return 5; }
        catch (DllNotFoundException) { return 7; }
        catch (TypeInitializationException error) when (error.InnerException is DllNotFoundException) { return 7; }
        catch { return 6; } // Never print PNG, OCR text, paths, or exception messages to logs/stderr.
    }
    private static Snip.Core.OcrResult Recognize(SKBitmap image, string folder, CancellationToken token)
    {
        OrtEnv.Instance().DisableTelemetryEvents();
        using var engine = new RapidOcr();
        using var options = RapidOcr.GetDefaultSessionOptions(2);
        options.InterOpNumThreads = 1; options.EnableCpuMemArena = false;
        engine.InitModels(RapidOcrModelSet.PPOCRv6Small with {
            DetModelPath = Path.Combine(folder, "det.onnx"), RecModelPath = Path.Combine(folder, "rec.onnx"),
            ClsModelPath = Path.Combine(folder, "cls.onnx"), KeysPath = Path.Combine(folder, "keys.txt") }, options);
        var regions = new List<OcrTextRegion>();
        const int tileHeight = 768, overlap = 128;
        bool tile = image.Height > 1800 || (long)image.Width * image.Height > 3_000_000;
        int step = tile ? tileHeight - overlap : image.Height;
        for (int y = 0; y < image.Height; y += step)
        {
            token.ThrowIfCancellationRequested();
            int height = tile ? Math.Min(tileHeight, image.Height - y) : image.Height;
            using var slice = new SKBitmap();
            if (!image.ExtractSubset(slice, new SKRectI(0, y, image.Width, y + height))) throw new InvalidDataException("增强识别无法读取图片分块。");
            var settings = RapidOcrOptions.PPOCRv6 with { DoAngle = false, LimitSideLen = OcrImagePreparation.DetectorShortSide(slice.Width, slice.Height) };
            var result = engine.Detect(slice, settings, token);
            foreach (var line in result.TextBlocks)
            {
                if (string.IsNullOrWhiteSpace(line.Text)) continue;
                double x = line.BoxPoints.Min(p => p.X), top = line.BoxPoints.Min(p => p.Y), right = line.BoxPoints.Max(p => p.X), bottom = line.BoxPoints.Max(p => p.Y);
                double center = (top + bottom) / 2;
                if (tile && (y > 0 && center < overlap / 2 || y + height < image.Height && center >= height - overlap / 2)) continue;
                var bounds = new PixelRect(x, top + y, right - x, bottom - top).Intersect(new(0, 0, image.Width, image.Height));
                if (bounds.IsEmpty) continue;
                double? confidence = line.CharScores is { Length: > 0 } scores ? scores.Average() : null;
                regions.Add(new(line.Text, bounds, confidence));
            }
            if (y + height >= image.Height) break;
        }
        return new(regions.OrderBy(r => r.Bounds.Y).ThenBy(r => r.Bounds.X).ToArray());
    }
    [DllImport("kernel32.dll")] private static extern uint SetErrorMode(uint mode);
}
