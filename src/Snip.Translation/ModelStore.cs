using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Snip.Translation;

public sealed record ModelFile(string Path, string Sha256, long Size);
public sealed record NativeRuntime(string Platform, string Version, string Url, ModelFile Archive, string EntryDirectory, string Executable, ModelFile[] Files);
public sealed record ModelInfo(string Name, string Repository, string Revision, string License, ModelFile Weight, NativeRuntime[] Runtimes);
public sealed record DownloadProgress(string Direction, string File, long CompletedBytes, long TotalBytes);

/// <summary>One bilingual model and pinned native CPU runtime, verified before use.</summary>
public sealed class ModelStore(string directory)
{
    private static readonly SemaphoreSlim installGate = new(1);
    private static readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(15) };
    public static ModelInfo Model { get; } = Load();
    public static bool NeedsWindowsRuntime => OperatingSystem.IsWindows() && new[] { "msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll" }
        .Any(name => !File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), name)));
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    public NativeRuntime Runtime => Model.Runtimes.SingleOrDefault(r => r.Platform == Platform)
        ?? throw new PlatformNotSupportedException("离线翻译目前支持 Windows x64。请使用 x64 版本程序。");
    private static string Platform => RuntimeInformation.ProcessArchitecture == Architecture.X64
        ? OperatingSystem.IsWindows() ? "win-x64" : OperatingSystem.IsLinux() ? "linux-x64" : "unsupported" : "unsupported";
    public string ModelPath => Path.Combine(DirectoryPath, "Hy-MT2-1.8B", Model.Weight.Path);
    public string RuntimeDirectory => Path.Combine(DirectoryPath, "Hy-MT2-1.8B", Runtime.Version, Runtime.Platform);
    public string ExecutablePath => Path.Combine(RuntimeDirectory, Runtime.Executable);
    /// <summary>Fast presence/size check for UI fallback. Full SHA256 verification still runs before inference.</summary>
    public bool HasInstalledFiles
    {
        get
        {
            try
            {
                bool Matches(string path, ModelFile file) => File.Exists(path) && new FileInfo(path).Length == file.Size;
                return Matches(ModelPath, Model.Weight) && Runtime.Files.All(file => Matches(Path.Combine(RuntimeDirectory, file.Path), file));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return false; }
        }
    }
    private static ModelInfo Load()
    {
        using var stream = typeof(ModelStore).Assembly.GetManifestResourceStream("Snip.Translation.translation-models.json")!;
        return JsonSerializer.Deserialize<ModelInfo>(stream)!;
    }
    public string Folder(string direction) { ValidateDirection(direction); return Path.GetDirectoryName(ModelPath)!; }
    public static void ValidateDirection(string direction)
    { if (direction is not "en-zh" and not "zh-en") throw new ArgumentException("离线翻译仅支持中英互译。"); }
    public async Task<bool> IsInstalledAsync(string direction, CancellationToken token)
    {
        ValidateDirection(direction);
        if (!await ValidAsync(ModelPath, Model.Weight, token).ConfigureAwait(false)) return false;
        return await RuntimeInstalledAsync(token).ConfigureAwait(false);
    }
    private async Task<bool> RuntimeInstalledAsync(CancellationToken token)
    {
        foreach (var file in Runtime.Files)
            if (!await ValidAsync(Path.Combine(RuntimeDirectory, file.Path), file, token).ConfigureAwait(false)) return false;
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
        await installGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var runtime = Runtime;
            long total = Model.Weight.Size + runtime.Archive.Size;
            var url = $"https://huggingface.co/{Model.Repository}/resolve/{Model.Revision}/{Model.Weight.Path}";
            await DownloadAsync(url, ModelPath, Model.Weight, 0, total, progress, token).ConfigureAwait(false);
            if (!await RuntimeInstalledAsync(token).ConfigureAwait(false))
            {
                string archive = Path.Combine(DirectoryPath, "Hy-MT2-1.8B", runtime.Archive.Path);
                await DownloadAsync(runtime.Url, archive, runtime.Archive, Model.Weight.Size, total, progress, token).ConfigureAwait(false);
                string temporary = RuntimeDirectory + ".install-" + Guid.NewGuid().ToString("N");
                try
                {
                    Directory.CreateDirectory(temporary);
                    if (runtime.Platform == "win-x64") ZipFile.ExtractToDirectory(archive, temporary);
                    else
                    {
                        await using var stream = File.OpenRead(archive);
                        await using var gzip = new GZipStream(stream, CompressionMode.Decompress);
                        await TarFile.ExtractToDirectoryAsync(gzip, temporary, overwriteFiles: true, cancellationToken: token).ConfigureAwait(false);
                    }
                    string payload = Path.Combine(temporary, runtime.EntryDirectory);
                    // Hashes are embedded in the application, not a mutable local seal.
                    foreach (var file in runtime.Files)
                        if (!await ValidAsync(Path.Combine(payload, file.Path), file, token).ConfigureAwait(false))
                            throw new InvalidDataException("离线推理器文件校验失败，请重新下载。");
                    Directory.CreateDirectory(RuntimeDirectory);
                    foreach (var file in runtime.Files)
                    {
                        token.ThrowIfCancellationRequested();
                        File.Copy(Path.Combine(payload, file.Path), Path.Combine(RuntimeDirectory, file.Path), overwrite: true);
                        if (!OperatingSystem.IsWindows() && file.Path == runtime.Executable)
                            File.SetUnixFileMode(Path.Combine(RuntimeDirectory, file.Path), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    }
                }
                finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true); }
                File.Delete(archive);
            }
            progress?.Report(new("Hy-MT2 中英互译", "ready", total, total));
            await File.WriteAllTextAsync(Path.Combine(Folder("en-zh"), "MODEL-NOTICE.txt"),
                $"Tencent Hy-MT2-1.8B; Q3_K_M GGUF by Unsloth.\nSource: https://huggingface.co/tencent/Hy-MT2-1.8B\nQuantization: https://huggingface.co/{Model.Repository}/tree/{Model.Revision}\nModel license: {Model.License}\nNative runtime: llama.cpp {runtime.Version}, MIT; https://github.com/ggml-org/llama.cpp\nNot retrained by SelfSnip. Full notices are supplied with the application.\n", token).ConfigureAwait(false);
        }
        finally { installGate.Release(); }
    }
    private static async Task DownloadAsync(string url, string destination, ModelFile file, long offset, long total, IProgress<DownloadProgress>? progress, CancellationToken token)
    {
        if (await ValidAsync(destination, file, token).ConfigureAwait(false)) { progress?.Report(new("Hy-MT2 中英互译", file.Path, offset + file.Size, total)); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException("模型或推理器下载失败，请检查网络后重试。");
            await using (var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920]; long written = 0;
                for (int count; (count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0;)
                {
                    written += count;
                    if (written > file.Size) throw new InvalidDataException("下载文件体积超出清单。");
                    await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    progress?.Report(new("Hy-MT2 中英互译", file.Path, offset + written, total));
                }
            }
            if (!await ValidAsync(temporary, file, token).ConfigureAwait(false)) throw new InvalidDataException("下载文件校验失败，请重新下载。");
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
