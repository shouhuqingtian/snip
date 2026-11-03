using System.Net;
using System.Text;
using Snip.Core;
using Snip.Translation;

internal static class OnlineTranscriptionChecks
{
    public static async Task<int> RunAsync()
    {
        int passed = 0;
        try
        {
            using var audio = new RecordedVoice(VoiceText.PcmToWav(Pcm()));
            foreach (string language in new[] { "", "zh", "en" })
            {
                int calls = 0;
                using var client = new HttpClient(new Handler(async (request, token) =>
                {
                    calls++; Require(request.Method == HttpMethod.Post && request.RequestUri!.AbsoluteUri == "https://public.example.com/v1/audio/transcriptions" && request.Headers.Authorization?.Parameter == "PUBLIC-KEY", "ASR request target/key differs.");
                    Require(request.Content is MultipartFormDataContent, "ASR must upload multipart audio, not chat JSON.");
                    var parts = ((MultipartFormDataContent)request.Content!).ToArray();
                    var file = parts.Single(p => p.Headers.ContentDisposition!.Name!.Trim('"') == "file");
                    Require(file.Headers.ContentType!.MediaType == "audio/wav" && (await file.ReadAsByteArrayAsync(token)).SequenceEqual(audio.Wav), "ASR WAV changed.");
                    Require(await parts.Single(p => p.Headers.ContentDisposition!.Name!.Trim('"') == "model").ReadAsStringAsync(token) == "PUBLIC-ASR", "ASR model differs.");
                    Require(parts.Any(p => p.Headers.ContentDisposition!.Name!.Trim('"') == "language") == (language.Length > 0), "Auto language must be omitted.");
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"text\":\"中文 English\\n下一句\"}", Encoding.UTF8, "application/json") };
                }));
                string text = await new OnlineTranscriptionService(new(new("PUBLIC", "https://public.example.com/v1/audio/transcriptions", "PUBLIC-ASR", language), "PUBLIC-KEY"), client).TranscribeAsync(audio, default);
                Require(text == "中文 English 下一句" && calls == 1, "ASR result sanitization or request count differs."); passed++;
            }
            foreach (int code in new[] { 302, 400, 401, 403, 404, 429, 503 })
            {
                int calls = 0;
                using var client = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage((HttpStatusCode)code) { Content = new StringContent("PRIVATE-KEY PRIVATE-VOICE PRIVATE-RESPONSE") }); }));
                try { await Service(client).TranscribeAsync(audio, default); throw new Exception("HTTP failure accepted."); }
                catch (VoiceInputException e) { Require(!e.Message.Contains("PRIVATE") && calls == 1 && e.Failure == VoiceInputFailure.Service && e.NativeCode == code, "Provider data leaked, ASR was retried or HTTP diagnostics were lost."); passed++; }
            }
            foreach (string body in new[] { "invalid", "{}", "{\"text\":\"\"}", "{\"text\":4}" })
            {
                using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) })));
                try { await Service(client).TranscribeAsync(audio, default); throw new Exception("Malformed ASR accepted."); }
                catch (VoiceInputException) { passed++; }
            }
            using (var cancel = new CancellationTokenSource())
            using (var client = new HttpClient(new Handler(async (_, token) => { cancel.Cancel(); await Task.Delay(5000, token); return new(HttpStatusCode.OK); })))
            {
                try { await Service(client).TranscribeAsync(audio, cancel.Token); throw new Exception("ASR cancellation ignored."); }
                catch (OperationCanceledException) { passed++; }
            }
            foreach (Exception failure in new Exception[] { new HttpRequestException("PRIVATE-ENDPOINT"), new IOException("PRIVATE-VOICE") })
            {
                using var client = new HttpClient(new Handler((_, _) => Task.FromException<HttpResponseMessage>(failure)));
                try { await Service(client).TranscribeAsync(audio, default); throw new Exception("ASR connection failure accepted."); }
                catch (VoiceInputException e) { Require(e.Failure == VoiceInputFailure.Network && ReferenceEquals(e.InnerException, failure) && !e.Message.Contains("PRIVATE"), "Connection diagnostics were lost or displayed private data."); passed++; }
            }
            Console.WriteLine($"{passed} deterministic voice transcription protocol checks passed; synthetic WAV only; no paid service request."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static OnlineTranscriptionService Service(HttpClient client) => new(new(new("PUBLIC", "https://public.example.com/v1/audio/transcriptions", "PUBLIC-ASR"), "PUBLIC-KEY"), client);
    internal static byte[] Pcm() { byte[] pcm = new byte[16000]; for (int i = 0; i < pcm.Length; i += 2) System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i), (short)(i % 3000 - 1500)); return pcm; }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
}
