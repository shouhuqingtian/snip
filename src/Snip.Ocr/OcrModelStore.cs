using System.Security.Cryptography;
using System.Text.Json;
using Snip.Core;

namespace Snip.Ocr;

public sealed record OcrModelFile(string Name, string RelativeUrl, long Size, string Sha256);

public sealed class OcrModelStore
{
    private const string Origin = "https://raw.githubusercontent.com/BobLd/RapidOcrNet/7b5c432e4262470b6d81bf1710c3bd52fc438566/RapidOcrNet/models/";
    private static readonly HttpClient http = CreateHttp();
    private static readonly SemaphoreSlim installGate = new(1);
    public static IReadOnlyList<OcrModelFile> Files { get; } = Array.AsReadOnly(new[] {
        new OcrModelFile("det.onnx", "v6/PP-OCRv6_det_small.onnx", 9929594, "090f04abcd9d9a7498bc4ebf677e4cb9bdce1fe4197ddb7e529f1ef44e1ff94f"),
        new OcrModelFile("rec.onnx", "v6/PP-OCRv6_rec_small.onnx", 21234383, "6f327246b50388f3c176ae304bd95767ea6dc0c9ae92153ef8cbe210b3c14884"),
        new OcrModelFile("cls.onnx", "v5/ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx", 1018508, "54379ae5174d026780215fc748a7f31910dee36818e63d49e17dc598ecc82df7"),
        new OcrModelFile("keys.txt", "v6/ppocrv6_dict.txt", 74947, "b5f2bfe2bdd9448429e3e82b51c789775d9b42f2403d082b00662eb77e401c5d") });
    public string DirectoryPath { get; }
    public OcrModelStore(string directory) => DirectoryPath = Path.GetFullPath(directory);
    public bool HasFiles
    {
        get
        {
            try { return Files.All(f => File.Exists(Path.Combine(DirectoryPath, f.Name)) && new FileInfo(Path.Combine(DirectoryPath, f.Name)).Length == f.Size); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
        }
    }
    public bool ReadEnabled()
    {
        try
        {
            var file = Path.Combine(DirectoryPath, "settings.json");
            return !File.Exists(file) || JsonSerializer.Deserialize<OcrSettings>(File.ReadAllText(file)) is { Version: 1, Enabled: true };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }
    public void SaveEnabled(bool value)
    {
        Directory.CreateDirectory(DirectoryPath);
        string target = Path.Combine(DirectoryPath, "settings.json"), temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(new OcrSettings(1, value))); File.Move(temporary, target, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public async Task<bool> ValidateAsync(CancellationToken token)
    {
        foreach (var file in Files)
            if (!await ValidFileAsync(Path.Combine(DirectoryPath, file.Name), file, token).ConfigureAwait(false)) return false;
        return true;
    }
    private static async Task<bool> ValidFileAsync(string path, OcrModelFile file, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!File.Exists(path) || new FileInfo(path).Length != file.Size) return false;
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase);
    }
    public async Task InstallAsync(IProgress<OcrDownloadProgress>? progress, CancellationToken token)
    {
        await installGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            long total = Files.Sum(f => f.Size), completed = 0;
            foreach (var file in Files)
            {
                token.ThrowIfCancellationRequested();
                string target = Path.Combine(DirectoryPath, file.Name);
                if (!await ValidFileAsync(target, file, token).ConfigureAwait(false))
                {
                    string temporary = target + "." + Guid.NewGuid().ToString("N") + ".download";
                    try
                    {
                        using var response = await OpenDownloadAsync(file, token).ConfigureAwait(false);
                        await using (var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
                        await using (var output = File.Create(temporary))
                        {
                            byte[] buffer = new byte[65536]; long received = 0; int count;
                            while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                            {
                                received += count; if (received > file.Size) throw new InvalidDataException("增强 OCR 模型大小不符。");
                                await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                                progress?.Report(new(completed + received, total));
                            }
                        }
                        if (!await ValidFileAsync(temporary, file, token).ConfigureAwait(false)) throw new InvalidDataException("增强 OCR 模型校验失败，请重试下载。");
                        token.ThrowIfCancellationRequested(); File.Move(temporary, target, true);
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
                completed += file.Size; progress?.Report(new(completed, total));
            }
        }
        finally { installGate.Release(); }
    }
    private static HttpClient CreateHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SelfSnip-OCR/2");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github.raw");
        return client;
    }
    private static async Task<HttpResponseMessage> OpenDownloadAsync(OcrModelFile file, CancellationToken token)
    {
        string? mirror = file.Name switch {
            "det.onnx" => "PP-OCRv6/det/PP-OCRv6_det_small.onnx",
            "rec.onnx" => "PP-OCRv6/rec/PP-OCRv6_rec_small.onnx",
            "cls.onnx" => "PP-OCRv5/cls/ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx", _ => null };
        var urls = new List<string>();
        if (mirror is not null) urls.Add("https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.10.0/onnx/" + mirror);
        urls.Add(Origin + file.RelativeUrl);
        if (file.Name == "keys.txt") urls.Add("https://api.github.com/repos/BobLd/RapidOcrNet/contents/RapidOcrNet/models/v6/ppocrv6_dict.txt?ref=7b5c432e4262470b6d81bf1710c3bd52fc438566");
        foreach (var url in urls)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var headersDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                headersDeadline.CancelAfter(TimeSpan.FromSeconds(25));
                var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, headersDeadline.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return response;
                response.Dispose();
            }
            catch (HttpRequestException) { }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        }
        throw new HttpRequestException("增强 OCR 模型下载服务暂不可用，请稍后重试；仍可使用系统 OCR。");
    }
    private sealed record OcrSettings(int Version, bool Enabled);
}
