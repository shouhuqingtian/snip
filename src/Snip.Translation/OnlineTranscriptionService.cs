using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Snip.Core;

namespace Snip.Translation;

public sealed class OnlineTranscriptionService(TranscriptionCredentials credentials, HttpClient? client = null) : IAudioTranscriber
{
    private static readonly HttpClient Shared = OnlineTransport.CreateClient();
    public async Task<string> TranscribeAsync(RecordedVoice audio, CancellationToken token)
    {
        TranscriptionProviders.Validate(credentials.Settings);
        if (string.IsNullOrWhiteSpace(credentials.ApiKey)) throw new VoiceInputException("请先在语音输入设置中填写识别服务密钥。");
        if (audio.Wav.Length < 44 || audio.Wav.Length > 44 + TranscriptionProviders.MaximumRecordingSeconds * 32000 || !audio.Wav.AsSpan(0, 4).SequenceEqual("RIFF"u8))
            throw new VoiceInputException("录音数据无效，请重新录音。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(90));
        using var request = new HttpRequestMessage(HttpMethod.Post, SpeechProviders.Endpoint(credentials.Settings.Endpoint));
        request.Headers.Authorization = new("Bearer", credentials.ApiKey);
        request.Content = Content(audio, credentials.Settings);
        try
        {
            using var response = await (client ?? Shared).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new VoiceInputException($"语音识别失败（HTTP {(int)response.StatusCode}）：" + (response.StatusCode switch
            {
                HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => "请求格式或参数不被接受，请核对所选接口协议、完整地址与语音识别模型。",
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "语音识别服务拒绝了密钥或权限，请检查配置。",
                HttpStatusCode.NotFound => "语音识别接口或模型不存在，请核对完整接口地址和模型名。",
                HttpStatusCode.MethodNotAllowed or HttpStatusCode.UnsupportedMediaType => "接口不接受当前语音上传方式，请核对所选接口协议。",
                HttpStatusCode.TooManyRequests => "语音识别服务限流或额度不足，请稍后重试。",
                >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest => "识别接口返回重定向，请填写最终 HTTPS 地址。",
                >= HttpStatusCode.InternalServerError => "语音识别服务暂时异常，请稍后重试或检查服务状态。",
                _ => "语音识别服务未完成请求，请检查模型、额度和服务状态。"
            }), VoiceInputFailure.Service, (int)response.StatusCode);
            if (response.Content.Headers.ContentLength > 131072) throw new VoiceInputException("识别服务返回内容过长。");
            using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false); using var output = new MemoryStream();
            byte[] buffer = new byte[4096];
            while (true)
            {
                int read = await stream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false); if (read == 0) break;
                if (output.Length + read > 131072) throw new VoiceInputException("识别服务返回内容过长。"); output.Write(buffer, 0, read);
            }
            using var json = JsonDocument.Parse(output.ToArray(), new JsonDocumentOptions { MaxDepth = 12 });
            return VoiceText.ForTyping(Text(json.RootElement, credentials.Settings.Protocol));
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested) { throw new VoiceInputException("语音识别超时，请稍后重试。", VoiceInputFailure.Network, innerException: error); }
        catch (HttpRequestException error) { throw new VoiceInputException("无法连接语音识别服务，请检查地址、代理、证书和网络。", VoiceInputFailure.Network, innerException: error); }
        catch (IOException error) { throw new VoiceInputException("语音识别连接中断，请稍后重试。", VoiceInputFailure.Network, innerException: error); }
        catch (JsonException error) { throw new VoiceInputException("识别服务返回格式不受支持，请检查所选语音识别协议。", VoiceInputFailure.Service, innerException: error); }
    }
    private static HttpContent Content(RecordedVoice audio, TranscriptionSettings settings)
    {
        if (settings.Protocol == TranscriptionProtocol.Qwen)
        {
            var options = new Dictionary<string, object> { ["enable_lid"] = true };
            if (settings.Language.Length > 0) options["language"] = settings.Language;
            return JsonContent.Create(new
            {
                model = settings.Model,
                input = new { messages = new[] { new { role = "user", content = new[] { new { audio = "data:audio/wav;base64," + Convert.ToBase64String(audio.Wav) } } } } },
                parameters = new { result_format = "message", asr_options = options }
            });
        }
        var content = new MultipartFormDataContent();
        content.Add(new StringContent(settings.Model), "model");
        if (settings.Protocol == TranscriptionProtocol.Zhipu) content.Add(new StringContent("false"), "stream");
        else if (settings.Language.Length > 0) content.Add(new StringContent(settings.Language), "language");
        var file = new ByteArrayContent(audio.Wav); file.Headers.ContentType = new("audio/wav"); content.Add(file, "file", "recording.wav");
        return content;
    }
    private static string Text(JsonElement root, TranscriptionProtocol protocol)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (protocol != TranscriptionProtocol.Qwen && root.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String) return text.GetString()!;
            var body = root;
            if (protocol == TranscriptionProtocol.Qwen && root.TryGetProperty("output", out var output)) body = output;
            if (protocol != TranscriptionProtocol.OpenAi && body.ValueKind == JsonValueKind.Object && body.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0 && choices[0].ValueKind == JsonValueKind.Object && choices[0].TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object && message.TryGetProperty("content", out var content))
            {
                if (content.ValueKind == JsonValueKind.String) return content.GetString()!;
                if (content.ValueKind == JsonValueKind.Array) return string.Concat(content.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.Object && p.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String).Select(p => p.GetProperty("text").GetString()));
            }
        }
        throw new VoiceInputException("识别服务未返回所选协议的文字结果，请检查接口协议。", VoiceInputFailure.Service);
    }
}
