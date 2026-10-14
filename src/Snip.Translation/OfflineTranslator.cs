using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Snip.Core;

namespace Snip.Translation;

public sealed record OfflineInferenceMetrics(int ProcessId, long PeakWorkingSetBytes, long PeakCombinedWorkingSetBytes, double ElapsedSeconds, bool ProcessExited, int? ExitCode);

public sealed class OfflineTranslator(ModelStore models) : ITranslationService
{
    // All result windows share a single native inference budget, even with separate service instances.
    private static readonly SemaphoreSlim gate = new(1);
    public event Action<OfflineInferenceMetrics>? InferenceCompleted;
    public Task<string> TranslateAsync(string text, string direction, CancellationToken token)
        => TranslateAsync(text, direction, null, token);
    public Task<string> TranslateAsync(string text, string direction, IProgress<TranslationProgress>? progress, CancellationToken token)
        => Task.Run(() => TranslateCoreAsync(text, direction, progress, token), token);
    private async Task<string> TranslateCoreAsync(string text, string direction, IProgress<TranslationProgress>? progress, CancellationToken callerToken)
    {
        callerToken.ThrowIfCancellationRequested(); ModelStore.ValidateDirection(direction);
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        if (text.Length > 12000) throw new ArgumentException("请分批翻译，每次不超过 12000 个字符。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(90)); var token = deadline.Token;
        await gate.WaitAsync(token).ConfigureAwait(false);
        NativeTranslationHost? host = null;
        var watch = Stopwatch.StartNew();
        try
        {
            progress?.Report(new("正在校验 Hy-MT2 翻译模型"));
            if (!await models.IsInstalledAsync(direction, token).ConfigureAwait(false))
                throw new InvalidOperationException("Hy-MT2 模型或推理器未安装/校验失败，请点击下载 / 修复模型。旧版 OPUS 模型不能用于新版本。");
            progress?.Report(new("正在加载 Hy-MT2 翻译模型"));
            host = new NativeTranslationHost(models);
            await host.WaitReadyAsync(token).ConfigureAwait(false);
            var paragraphs = Split(text); int total = paragraphs.Sum(p => p.Count), completed = 0;
            var results = new List<string>();
            foreach (var paragraph in paragraphs)
            {
                var translated = new List<string>();
                foreach (var segment in paragraph)
                {
                    token.ThrowIfCancellationRequested(); progress?.Report(new("正在准备翻译段落", completed, total));
                    string sourceLanguage = direction == "en-zh" ? "English" : "Chinese";
                    string target = direction == "en-zh" ? "Simplified Chinese" : "English";
                    var prompt = $"Translate the following {sourceLanguage} text into {target}. This is ordinary prose, not code. Translate greetings and common words; preserve only explicit URLs and code identifiers. Output only the translation:\n\n{segment}";
                    // Tokenize the actual rendered chat prompt so no input is silently truncated.
                    using var template = await host.PostAsync("apply-template", new { messages = new[] { new { role = "user", content = prompt } }, add_generation_prompt = true }, token).ConfigureAwait(false);
                    string rendered = template.RootElement.GetProperty("prompt").GetString()!;
                    using var tokens = await host.PostAsync("tokenize", new { content = rendered, add_special = true }, token).ConfigureAwait(false);
                    if (tokens.RootElement.GetProperty("tokens").GetArrayLength() > 1200)
                        throw new InvalidOperationException("这一段分词后过长，请缩短原文再翻译。已有结果保留。");
                    progress?.Report(new("正在翻译", completed, total));
                    using var response = await host.PostAsync("v1/chat/completions", new
                    {
                        model = "self-snip-offline", messages = new[] { new { role = "user", content = prompt } },
                        temperature = 0, max_tokens = 768, stream = false, cache_prompt = false
                    }, token).ConfigureAwait(false);
                    var choice = response.RootElement.GetProperty("choices")[0];
                    if (choice.GetProperty("finish_reason").GetString() != "stop")
                        throw new InvalidOperationException("译文达到长度上限，请缩短原文再试；未用截断内容替换已有结果。");
                    string output = choice.GetProperty("message").GetProperty("content").GetString()?.Trim() ?? "";
                    if (output.Length == 0) throw new InvalidOperationException("本地模型未返回译文，请缩短原文再试。");
                    translated.Add(output); completed++;
                    progress?.Report(new("正在翻译", completed, total));
                }
                results.Add(string.Join(direction == "zh-en" ? " " : "", translated));
            }
            token.ThrowIfCancellationRequested();
            return string.Join(Environment.NewLine, results);
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        { throw new TimeoutException("本地翻译超过 90 秒，已停止并释放模型；请分段翻译。"); }
        catch (Exception error) when (error is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException && token.IsCancellationRequested)
        { throw new OperationCanceledException(token); }
        catch (Exception error) when (error is HttpRequestException or JsonException or KeyNotFoundException)
        { throw new InvalidOperationException("本地推理器返回失败或格式错误，请下载 / 修复模型后重试。"); }
        finally
        {
            try
            {
                if (host is not null)
                {
                    await host.DisposeAsync().ConfigureAwait(false);
                    try { InferenceCompleted?.Invoke(new(host.ProcessId, host.PeakWorkingSetBytes, host.PeakCombinedWorkingSetBytes, watch.Elapsed.TotalSeconds, host.Exited, host.ExitCode)); } catch { }
                    progress?.Report(new("已释放本地模型"));
                }
            }
            finally { gate.Release(); }
        }
    }
    private static List<List<string>> Split(string text)
    {
        var result = new List<List<string>>();
        foreach (string paragraph in text.Replace("\r", "").Split('\n'))
        {
            var segments = new List<string>();
            for (int start = 0; start < paragraph.Length;)
            {
                int end = Math.Min(start + 600, paragraph.Length);
                if (end < paragraph.Length)
                {
                    int boundary = end;
                    while (boundary > start + 300 && !char.IsWhiteSpace(paragraph[boundary - 1]) && paragraph[boundary - 1] is not '。' and not '！' and not '？' and not '.' and not '!' and not '?') boundary--;
                    if (boundary > start + 300) end = boundary;
                    if (char.IsHighSurrogate(paragraph[end - 1])) end--;
                }
                string segment = paragraph[start..end].Trim(); if (segment.Length > 0) segments.Add(segment);
                start = end;
            }
            result.Add(segments);
        }
        return result;
    }
}
