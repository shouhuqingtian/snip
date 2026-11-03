using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Snip.Core;

namespace Snip.Translation;

public sealed class OnlineTranscriptionService(TranscriptionCredentials credentials, HttpClient? client = null) : IAudioTranscriber
{
    private static readonly HttpClient Shared = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };
    public async Task<string> TranscribeAsync(RecordedVoice audio, CancellationToken token)
    {
        TranscriptionProviders.Validate(credentials.Settings);
        if (string.IsNullOrWhiteSpace(credentials.ApiKey)) throw new VoiceInputException("请先在语音输入设置中填写识别服务密钥。");
        if (audio.Wav.Length < 44 || audio.Wav.Length > 44 + TranscriptionProviders.MaximumRecordingSeconds * 32000 || !audio.Wav.AsSpan(0, 4).SequenceEqual("RIFF"u8))
            throw new VoiceInputException("录音数据无效，请重新录音。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(90));
        using var request = new HttpRequestMessage(HttpMethod.Post, SpeechProviders.Endpoint(credentials.Settings.Endpoint));
        request.Headers.Authorization = new("Bearer", credentials.ApiKey);
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(credentials.Settings.Model), "model");
        if (credentials.Settings.Language.Length > 0) content.Add(new StringContent(credentials.Settings.Language), "language");
        var file = new ByteArrayContent(audio.Wav); file.Headers.ContentType = new("audio/wav"); content.Add(file, "file", "recording.wav"); request.Content = content;
        try
        {
            using var response = await (client ?? Shared).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new VoiceInputException($"语音识别失败（HTTP {(int)response.StatusCode}）：" + (response.StatusCode switch
            {
                HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => "请求格式或参数不被接受。自定义接口必须兼容 OpenAI 语音转文字协议，请核对接口与语音识别模型。",
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "语音识别服务拒绝了密钥或权限，请检查配置。",
                HttpStatusCode.NotFound => "语音识别接口或模型不存在，请核对完整 transcriptions 地址和模型名。",
                HttpStatusCode.MethodNotAllowed or HttpStatusCode.UnsupportedMediaType => "接口不接受当前语音上传方式，请使用 OpenAI 兼容的语音转文字接口。",
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
            if (!json.RootElement.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String) throw new VoiceInputException("识别服务没有返回 text 字段，请检查接口协议。");
            return VoiceText.ForTyping(text.GetString()!);
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested) { throw new VoiceInputException("语音识别超时，请稍后重试。", VoiceInputFailure.Network, innerException: error); }
        catch (HttpRequestException error) { throw new VoiceInputException("无法连接语音识别服务，请检查地址、代理、证书和网络。", VoiceInputFailure.Network, innerException: error); }
        catch (IOException error) { throw new VoiceInputException("语音识别连接中断，请稍后重试。", VoiceInputFailure.Network, innerException: error); }
        catch (JsonException error) { throw new VoiceInputException("识别服务返回格式不受支持，请使用兼容语音转文字接口。", VoiceInputFailure.Service, innerException: error); }
    }
}
