using System.Security.Cryptography;
using System.Text.Json;

namespace Snip.Translation;

public sealed record ModelFile(string Path, string Sha256, long Size);
public sealed record ModelInfo(string Direction, string Repository, string Revision, string License, ModelFile[] Files);
public sealed record DownloadProgress(string Direction, string File, long CompletedBytes, long TotalBytes);

/// <summary>Models are pinned to immutable revisions and checked before local inference.</summary>
public sealed class ModelStore(string directory)
{
    private readonly SemaphoreSlim gate = new(1);
    private static readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(15) };
    public static IReadOnlyList<ModelInfo> Models { get; } = Load();
    public string DirectoryPath { get; } = System.IO.Path.GetFullPath(directory);
    private static ModelInfo[] Load()
    {
        using var stream = typeof(ModelStore).Assembly.GetManifestResourceStream("Snip.Translation.translation-models.json")!;
        return JsonSerializer.Deserialize<ModelInfo[]>(stream)!;
    }
    public string Folder(string direction) => Path.Combine(DirectoryPath, Models.Single(m => m.Direction == direction).Direction);
    public async Task<bool> IsInstalledAsync(string direction, CancellationToken token)
    {
        foreach (var file in Models.Single(m => m.Direction == direction).Files)
            if (!await ValidAsync(Path.Combine(Folder(direction), file.Path), file, token)) return false;
        return true;
    }
    private static async Task<bool> ValidAsync(string path, ModelFile file, CancellationToken token)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != file.Size) return false;
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(input, token).ConfigureAwait(false);
        return Convert.ToHexString(hash).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase);
    }
    public async Task InstallAsync(IProgress<DownloadProgress>? progress, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            long completed = 0, total = Models.Sum(m => m.Files.Sum(f => f.Size));
            foreach (var model in Models) foreach (var file in model.Files)
            {
                var destination = Path.Combine(Folder(model.Direction), file.Path);
                if (await ValidAsync(destination, file, token)) { completed += file.Size; progress?.Report(new(model.Direction, file.Path, completed, total)); continue; }
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                var temporary = destination + ".partial-" + Guid.NewGuid().ToString("N");
                try
                {
                    var url = $"https://huggingface.co/{model.Repository}/resolve/{model.Revision}/{file.Path}";
                    using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token); response.EnsureSuccessStatusCode();
                    await using (var input = await response.Content.ReadAsStreamAsync(token))
                    await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                    {
                        var buffer = new byte[81920]; long written = 0;
                        for (int n; (n = await input.ReadAsync(buffer, token)) > 0;)
                        {
                            written += n; if (written > file.Size) throw new InvalidDataException("模型文件体积超出清单。");
                            await output.WriteAsync(buffer.AsMemory(0, n), token); progress?.Report(new(model.Direction, file.Path, completed + written, total));
                        }
                    }
                    if (!await ValidAsync(temporary, file, token)) throw new InvalidDataException("模型校验失败，请重试下载。");
                    File.Move(temporary, destination, true); completed += file.Size;
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            foreach (var model in Models)
                await File.WriteAllTextAsync(Path.Combine(Folder(model.Direction), "MODEL-NOTICE.txt"),
                    $"OPUS-MT / Helsinki-NLP; ONNX quantization by Xenova.\nSource: https://huggingface.co/Helsinki-NLP/opus-mt-{model.Direction}\nConversion: https://huggingface.co/{model.Repository}/tree/{model.Revision}\nLicense: {model.License}\nModels converted and quantized by Xenova, not retrained by SelfSnip.\n", token);
        }
        finally { gate.Release(); }
    }
}
