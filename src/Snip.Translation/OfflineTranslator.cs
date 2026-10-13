using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using Snip.Core;

namespace Snip.Translation;

public sealed class OfflineTranslator(ModelStore models) : ITranslationService
{
    private readonly SemaphoreSlim gate = new(1);
    public Task<string> TranslateAsync(string text, string direction, CancellationToken token)
        => TranslateAsync(text, direction, null, token);
    public Task<string> TranslateAsync(string text, string direction, IProgress<TranslationProgress>? progress, CancellationToken token)
        => Task.Run(() => TranslateCoreAsync(text, direction, progress, token), token);
    private async Task<string> TranslateCoreAsync(string text, string direction, IProgress<TranslationProgress>? progress, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        if (text.Length > 12000) throw new ArgumentException("请分批翻译，每次不超过 12000 个字符。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(90)); token = deadline.Token;
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            progress?.Report(new("正在校验翻译模型"));
            if (!await models.IsInstalledAsync(direction, token).ConfigureAwait(false)) throw new InvalidOperationException("翻译模型未安装或校验失败，请先下载/修复模型。");
            return Run(text, direction, progress, token);
        }
        catch (OnnxRuntimeException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        finally { gate.Release(); }
    }
    private string Run(string text, string direction, IProgress<TranslationProgress>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); progress?.Report(new("正在加载翻译模型"));
        string folder = models.Folder(direction);
        var vocabulary = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(Path.Combine(folder, "vocab.json")))!;
        var reverse = vocabulary.ToDictionary(x => x.Value, x => x.Key);
        using var sourceStream = File.OpenRead(Path.Combine(folder, "source.spm"));
        using var targetStream = File.OpenRead(Path.Combine(folder, "target.spm"));
        var sourceTokenizer = SentencePieceTokenizer.Create(sourceStream, false, false);
        var targetTokenizer = SentencePieceTokenizer.Create(targetStream, false, false);
        using var options = new SessionOptions { IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount - 1, 1, 2), InterOpNumThreads = 1, GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        options.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");
        using var encoder = new InferenceSession(Path.Combine(folder, "onnx/encoder_model_quantized.onnx"), options);
        using var decoder = new InferenceSession(Path.Combine(folder, "onnx/decoder_model_quantized.onnx"), options);
        using var configuration = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "config.json")));
        int eos = configuration.RootElement.GetProperty("eos_token_id").GetInt32();
        int start = configuration.RootElement.GetProperty("decoder_start_token_id").GetInt32();
        using var runOptions = new RunOptions();
        using var cancellation = token.Register(() => runOptions.Terminate = true);
        var paragraphs = text.Replace("\r", "").Split('\n').Select(paragraph => string.IsNullOrWhiteSpace(paragraph) ? [] :
            sourceTokenizer.EncodeToTokens(paragraph, out _, false, false).Select(t => (long)(vocabulary.TryGetValue(t.Value, out int id) ? id : vocabulary["<unk>"])).Chunk(240).ToArray()).ToArray();
        int completed = 0, total = paragraphs.Sum(p => p.Length);
        var results = new List<string>();
        foreach (var paragraph in paragraphs)
        {
            if (paragraph.Length == 0) { results.Add(""); continue; }
            var segments = new List<string>();
            foreach (var segment in paragraph)
            {
                token.ThrowIfCancellationRequested();
                var ids = segment.ToList();
                ids.Add(eos);
                var input = new DenseTensor<long>(ids.ToArray(), [1, ids.Count]);
                var attention = new DenseTensor<long>(Enumerable.Repeat(1L, ids.Count).ToArray(), [1, ids.Count]);
                using var hidden = encoder.Run([NamedOnnxValue.CreateFromTensor("input_ids", input), NamedOnnxValue.CreateFromTensor("attention_mask", attention)], encoder.OutputNames, runOptions);
                var states = hidden.First().AsTensor<float>();
                progress?.Report(new("正在翻译", completed, total));
                var generated = Generate(decoder, states, attention, start, eos, runOptions,
                    count => progress?.Report(new("正在翻译", completed, total, count)), token);
                var targetIds = generated.Skip(1).Select(id => reverse.GetValueOrDefault((int)id, "<unk>"))
                    .Where(piece => !piece.StartsWith(">>", StringComparison.Ordinal))
                    .Select(piece => targetTokenizer.Vocabulary.TryGetValue(piece, out int id) ? id : targetTokenizer.UnknownId);
                segments.Add(targetTokenizer.Decode(targetIds, true).Trim());
                completed++; progress?.Report(new("正在翻译", completed, total));
            }
            results.Add(string.Join(direction == "zh-en" ? " " : "", segments));
        }
        return string.Join(Environment.NewLine, results);
    }
    private static IReadOnlyList<long> Generate(InferenceSession decoder, Tensor<float> states, DenseTensor<long> attention, int start, int eos, RunOptions runOptions, Action<int> progress, CancellationToken token)
    {
        var beams = new List<(List<long> Ids, double Score)> { (new List<long> { start }, 0) };
        var finished = new List<(List<long> Ids, double Score)>();
        for (int step = 0; step < 300; step++)
        {
            var candidates = new List<(List<long> Ids, double Score)>();
            foreach (var beam in beams)
            {
                token.ThrowIfCancellationRequested();
                using var output = decoder.Run([
                    NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(beam.Ids.ToArray(), [1, beam.Ids.Count])),
                    NamedOnnxValue.CreateFromTensor("encoder_hidden_states", states),
                    NamedOnnxValue.CreateFromTensor("encoder_attention_mask", attention)], decoder.OutputNames, runOptions);
                var logits = output.First(x => x.Name == "logits").AsTensor<float>();
                int size = logits.Dimensions[^1], offset = (beam.Ids.Count - 1) * size;
                // Read only the newest token. Copying the entire [length,vocabulary] tensor
                // for every beam caused large repeated allocations and GC pauses.
                var values = logits is DenseTensor<float> dense ? dense.Buffer.Span.Slice(offset, size) : logits.ToArray().AsSpan(offset, size);
                var top = new List<(int Id, float Value)>();
                float maximum = float.NegativeInfinity;
                for (int id = 0; id < size; id++)
                {
                    if (id == start) continue;
                    maximum = Math.Max(maximum, values[id]);
                    if (top.Count < 4 || values[id] > top[^1].Value)
                    {
                        int insert = 0;
                        while (insert < top.Count && top[insert].Value >= values[id]) insert++;
                        top.Insert(insert, (id, values[id])); if (top.Count > 4) top.RemoveAt(4);
                    }
                }
                double denominator = 0;
                for (int id = 0; id < size; id++) if (id != start) denominator += Math.Exp(values[id] - maximum);
                foreach (var next in top)
                {
                    var score = beam.Score + next.Value - maximum - Math.Log(denominator);
                    var ids = new List<long>(beam.Ids);
                    if (next.Id == eos) finished.Add((ids, score));
                    else { ids.Add(next.Id); candidates.Add((ids, score)); }
                }
            }
            beams = candidates.OrderByDescending(x => x.Score).Take(4).ToList();
            if (step % 5 == 0) progress(step + 1);
            if (finished.Count >= 4 || beams.Count == 0) break;
        }
        if (finished.Count == 0) throw new InvalidOperationException("翻译达到长度上限，请将原文分成更短段落重试。");
        return finished.OrderByDescending(x => x.Score / Math.Max(1, x.Ids.Count - 1)).First().Ids;
    }

}
