using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using Snip.Core;

namespace Snip.Translation;

public sealed class OfflineTranslator(ModelStore models) : ITranslationService
{
    private readonly SemaphoreSlim gate = new(1);
    public async Task<string> TranslateAsync(string text, string direction, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        if (text.Length > 12000) throw new ArgumentException("请分批翻译，每次不超过 12000 个字符。");
        await gate.WaitAsync(token);
        try
        {
            if (!await models.IsInstalledAsync(direction, token)) throw new InvalidOperationException("翻译模型未安装或校验失败，请先下载/修复模型。");
            return await Task.Run(() => Run(text, direction, token), token);
        }
        finally { gate.Release(); }
    }
    private string Run(string text, string direction, CancellationToken token)
    {
        string folder = models.Folder(direction);
        var vocabulary = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(Path.Combine(folder, "vocab.json")))!;
        var reverse = vocabulary.ToDictionary(x => x.Value, x => x.Key);
        using var sourceStream = File.OpenRead(Path.Combine(folder, "source.spm"));
        using var targetStream = File.OpenRead(Path.Combine(folder, "target.spm"));
        var sourceTokenizer = SentencePieceTokenizer.Create(sourceStream, false, false);
        var targetTokenizer = SentencePieceTokenizer.Create(targetStream, false, false);
        using var options = new SessionOptions { IntraOpNumThreads = Math.Min(4, Environment.ProcessorCount), InterOpNumThreads = 1, GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        using var encoder = new InferenceSession(Path.Combine(folder, "onnx/encoder_model_quantized.onnx"), options);
        using var decoder = new InferenceSession(Path.Combine(folder, "onnx/decoder_model_quantized.onnx"), options);
        using var configuration = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "config.json")));
        int eos = configuration.RootElement.GetProperty("eos_token_id").GetInt32();
        int start = configuration.RootElement.GetProperty("decoder_start_token_id").GetInt32();
        var results = new List<string>();
        foreach (var paragraph in text.Replace("\r", "").Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(paragraph)) { results.Add(""); continue; }
            var encoded = sourceTokenizer.EncodeToTokens(paragraph, out _, false, false).Select(t => (long)(vocabulary.TryGetValue(t.Value, out int id) ? id : vocabulary["<unk>"])).ToArray();
            var segments = new List<string>();
            foreach (var segment in encoded.Chunk(240))
            {
                token.ThrowIfCancellationRequested();
                var ids = segment.ToList();
                ids.Add(eos);
                var input = new DenseTensor<long>(ids.ToArray(), [1, ids.Count]);
                var attention = new DenseTensor<long>(Enumerable.Repeat(1L, ids.Count).ToArray(), [1, ids.Count]);
                using var hidden = encoder.Run([NamedOnnxValue.CreateFromTensor("input_ids", input), NamedOnnxValue.CreateFromTensor("attention_mask", attention)]);
                var states = hidden.First().AsTensor<float>();
                var generated = Generate(decoder, states, attention, start, eos, token);
                var targetIds = generated.Skip(1).Select(id => reverse.GetValueOrDefault((int)id, "<unk>"))
                    .Where(piece => !piece.StartsWith(">>", StringComparison.Ordinal))
                    .Select(piece => targetTokenizer.Vocabulary.TryGetValue(piece, out int id) ? id : targetTokenizer.UnknownId);
                segments.Add(targetTokenizer.Decode(targetIds, true).Trim());
            }
            results.Add(string.Join(direction == "zh-en" ? " " : "", segments));
        }
        return string.Join(Environment.NewLine, results);
    }
    private static IReadOnlyList<long> Generate(InferenceSession decoder, Tensor<float> states, DenseTensor<long> attention, int start, int eos, CancellationToken token)
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
                    NamedOnnxValue.CreateFromTensor("encoder_attention_mask", attention)]);
                var logits = output.First(x => x.Name == "logits").AsTensor<float>();
                int size = logits.Dimensions[^1], offset = (beam.Ids.Count - 1) * size;
                var values = logits.ToArray(); var top = new List<(int Id, float Value)>();
                float maximum = float.NegativeInfinity;
                for (int id = 0; id < size; id++)
                {
                    if (id == start) continue;
                    maximum = Math.Max(maximum, values[offset + id]);
                    if (top.Count < 4 || values[offset + id] > top[^1].Value)
                    { top.Add((id, values[offset + id])); top = top.OrderByDescending(x => x.Value).Take(4).ToList(); }
                }
                double denominator = 0;
                for (int id = 0; id < size; id++) if (id != start) denominator += Math.Exp(values[offset + id] - maximum);
                foreach (var next in top)
                {
                    var score = beam.Score + next.Value - maximum - Math.Log(denominator);
                    var ids = new List<long>(beam.Ids);
                    if (next.Id == eos) finished.Add((ids, score));
                    else { ids.Add(next.Id); candidates.Add((ids, score)); }
                }
            }
            beams = candidates.OrderByDescending(x => x.Score).Take(4).ToList();
            if (finished.Count >= 4 || beams.Count == 0) break;
        }
        if (finished.Count == 0) throw new InvalidOperationException("翻译达到长度上限，请将原文分成更短段落重试。");
        return finished.OrderByDescending(x => x.Score / Math.Max(1, x.Ids.Count - 1)).First().Ids;
    }

}
