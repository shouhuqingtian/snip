using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Snip.Core;

namespace Snip.Translation;

/// <summary>One pinned credential snapshot per playback. No automatic retry, redirect or provider failover.</summary>
public sealed class OnlineSpeechSynthesizer(OnlineSpeechCredentials credentials, HttpClient? client = null) : IOnlineSpeechSynthesizer
{
    private const int MaximumAudio = 12 * 1024 * 1024, MaximumJson = 32 * 1024 * 1024;
    private static readonly HttpClient Shared = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient http = client ?? Shared;
    public async Task<SpeechAudio> SynthesizeAsync(string text, string language, CancellationToken token)
    {
        SpeechProviders.Validate(credentials.Settings);
        if (string.IsNullOrWhiteSpace(text) || text.Length > 1000 || language is not ("en" or "zh")) throw new SpeechServiceException("语音文字为空或过长，请分段朗读。");
        if (string.IsNullOrWhiteSpace(credentials.ApiKey) || credentials.ApiKey.Length > 8192 || credentials.ApiKey.Any(char.IsControl)) throw new SpeechServiceException("语音密钥无效，请在 AI 语音设置中重新填写。");
        var settings = credentials.Settings; var endpoint = SpeechProviders.Endpoint(settings.Endpoint);
        object body = settings.Protocol switch
        {
            SpeechProtocol.OpenAi => new { model = settings.Model, input = text, voice = settings.Voice, response_format = "mp3" },
            SpeechProtocol.Qwen => new { model = settings.Model, input = new { text, voice = settings.Voice, language_type = language == "zh" ? "Chinese" : "English" } },
            SpeechProtocol.MiniMax => new { model = settings.Model, text, stream = false, language_boost = language == "zh" ? "Chinese" : "English", output_format = "hex",
                voice_setting = new { voice_id = settings.Voice, speed = 1, vol = 1, pitch = 0 }, audio_setting = new { sample_rate = 32000, bitrate = 128000, format = "mp3", channel = 1 } },
            _ => throw new SpeechServiceException("语音接口协议无效，请检查配置。")
        };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.ApiKey);
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            CheckStatus(response);
            if (settings.Protocol == SpeechProtocol.OpenAi)
                return Audio(await ReadBoundedAsync(response, MaximumAudio, deadline.Token).ConfigureAwait(false));
            byte[] json = await ReadBoundedAsync(response, MaximumJson, deadline.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 24 });
            var root = document.RootElement;
            if (settings.Protocol == SpeechProtocol.MiniMax)
            {
                if (!root.TryGetProperty("base_resp", out var status) || !status.TryGetProperty("status_code", out var code) || !code.TryGetInt32(out int value) || value != 0)
                    throw new SpeechServiceException("MiniMax 未完成语音合成，请检查密钥、模型、音色或余额。");
                string? hex = root.GetProperty("data").GetProperty("audio").GetString();
                if (string.IsNullOrEmpty(hex) || hex.Length > MaximumAudio * 2 || hex.Length % 2 != 0) throw new SpeechServiceException("服务返回的音频为空或过大，请缩短文字。");
                return Audio(Convert.FromHexString(hex));
            }
            string? address = root.GetProperty("output").GetProperty("audio").GetProperty("url").GetString();
            if (!Uri.TryCreate(address, UriKind.Absolute, out var audioUri) || audioUri.UserInfo.Length > 0 || audioUri.Fragment.Length > 0 ||
                !(audioUri.Scheme == "https" && (SpeechProviders.SameService(audioUri, endpoint) || audioUri.Host.EndsWith(".aliyuncs.com", StringComparison.OrdinalIgnoreCase)) || endpoint.IsLoopback && SpeechProviders.SameService(audioUri, endpoint)))
                throw new SpeechServiceException("语音服务未返回有效的同源或阿里云音频地址，请检查语音接口协议。");
            // Never forward the API key to the audio CDN, including on redirects.
            using var download = new HttpRequestMessage(HttpMethod.Get, audioUri);
            using var audioResponse = await http.SendAsync(download, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            CheckStatus(audioResponse);
            return Audio(await ReadBoundedAsync(audioResponse, MaximumAudio, deadline.Token).ConfigureAwait(false));
        }
        catch (SpeechServiceException) { throw; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new SpeechServiceException("在线语音超过 60 秒未返回，请重试或手动切回系统语音。"); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is HttpRequestException or IOException)
        { throw new SpeechServiceException("无法连接或读取在线语音服务，请检查网络或切换配置；文字已保留。"); }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new SpeechServiceException("语音响应格式无效，请检查接口协议、模型和音色；文字已保留。"); }
    }
    private static void CheckStatus(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        throw new SpeechServiceException((int)response.StatusCode switch
        {
            401 or 403 => "语音服务拒绝访问，请检查密钥、地域和模型权限。",
            429 => "语音服务请求受限或余额不足，请稍后重试或切换语音配置。",
            >= 300 and < 400 => "语音服务返回了重定向，已停止请求；请填写最终语音 API 地址。",
            _ => "语音服务未完成请求，请检查接口协议、模型和音色或稍后重试。"
        });
    }
    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int limit, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > limit) throw new SpeechServiceException("语音响应过大，请缩短文字。");
        using var output = new MemoryStream(); using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var buffer = new byte[16384]; int read;
        while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (output.Length + read > limit) throw new SpeechServiceException("语音响应过大，请缩短文字。");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
    private static SpeechAudio Audio(byte[] bytes)
    {
        if (bytes.Length >= 44 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8)) return new(bytes, "audio/wav");
        if (bytes.Length >= 10 && (bytes.AsSpan(0, 3).SequenceEqual("ID3"u8) || bytes[0] == 255 && (bytes[1] & 224) == 224)) return new(bytes, "audio/mpeg");
        throw new SpeechServiceException("服务返回的内容不是支持的 MP3/WAV 音频，请检查语音 API 地址和协议。");
    }
}
