using System.Net;
using System.Text;
using System.Text.Json;
using Snip.Core;
using Snip.Translation;

internal static class OnlineSpeechChecks
{
    internal static byte[] Wave()
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        const int rate = 16000, samples = rate / 5;
        writer.Write("RIFF"u8); writer.Write(36 + samples * 2); writer.Write("WAVEfmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(rate); writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(samples * 2);
        for (int i = 0; i < samples; i++) writer.Write((short)(Math.Sin(i * Math.PI * 2 * 440 / rate) * 400));
        return stream.ToArray();
    }
    public static async Task<int> RunAsync()
    {
        int passed = 0;
        async Task Check(string name, Func<Task> action) { await action(); passed++; Console.WriteLine("PASS AI speech " + name); }
        OnlineSpeechCredentials Credentials(SpeechProtocol protocol) => new(new("PUBLIC-VOICE", protocol, "https://speech.example.com/tts", "PUBLIC-MODEL", "PUBLIC-VOICE"), "PUBLIC-SECRET");
        try
        {
            foreach (var protocol in Enum.GetValues<SpeechProtocol>())
                await Check(protocol + " request and bounded audio; CDN never receives credentials", async () =>
                {
                    int calls = 0;
                    using var client = new HttpClient(new Handler(async (request, token) =>
                    {
                        calls++;
                        if (request.Method == HttpMethod.Get)
                        {
                            Require(protocol == SpeechProtocol.Qwen && request.Headers.Authorization is null, "Audio CDN received credentials.");
                            return Binary();
                        }
                        Require(request.Headers.Authorization?.Parameter == "PUBLIC-SECRET" && request.RequestUri!.AbsoluteUri == "https://speech.example.com/tts", "Wrong API key/endpoint.");
                        using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)); var body = json.RootElement;
                        Require(body.GetProperty("model").GetString() == "PUBLIC-MODEL", "Wrong speech model.");
                        if (protocol == SpeechProtocol.OpenAi)
                        {
                            Require(body.GetProperty("input").GetString() == "PUBLIC-TEXT" && body.GetProperty("voice").GetString() == "PUBLIC-VOICE" && body.GetProperty("response_format").GetString() == "mp3", "OpenAI speech schema differs.");
                            return Binary();
                        }
                        if (protocol == SpeechProtocol.MiniMax)
                        {
                            Require(!body.GetProperty("stream").GetBoolean() && body.GetProperty("output_format").GetString() == "hex" && body.GetProperty("voice_setting").GetProperty("voice_id").GetString() == "PUBLIC-VOICE" && body.GetProperty("text").GetString() == "PUBLIC-TEXT", "MiniMax speech schema differs.");
                            return Json(new { base_resp = new { status_code = 0 }, data = new { audio = Convert.ToHexString(Wave()) } });
                        }
                        Require(body.GetProperty("input").GetProperty("language_type").GetString() == "Chinese" && body.GetProperty("input").GetProperty("text").GetString() == "PUBLIC-TEXT", "Qwen speech schema differs.");
                        return Json(new { output = new { audio = new { url = "https://public.oss-cn-beijing.aliyuncs.com/audio.wav?temporary=public" } } });
                    }));
                    var audio = await new OnlineSpeechSynthesizer(Credentials(protocol), client).SynthesizeAsync("PUBLIC-TEXT", "zh", CancellationToken.None);
                    Require(audio.ContentType == "audio/wav" && audio.Data.SequenceEqual(Wave()) && calls == (protocol == SpeechProtocol.Qwen ? 2 : 1), "Audio changed or request retried.");
                });
            foreach (var code in new[] { 302, 400, 401, 403, 429, 503 })
                await Check("HTTP " + code + " has no retry, redirect or provider-body disclosure", async () =>
                {
                    int calls = 0;
                    using var client = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage((HttpStatusCode)code) { Content = new StringContent("PRIVATE-BODY PUBLIC-SECRET PUBLIC-TEXT") }); }));
                    await Reject(() => new OnlineSpeechSynthesizer(Credentials(SpeechProtocol.OpenAi), client).SynthesizeAsync("PUBLIC-TEXT", "en", CancellationToken.None));
                    Require(calls == 1, "Failure caused an automatic retry.");
                });
            await Check("cancellation interrupts pending requests", async () =>
            {
                using var cancel = new CancellationTokenSource();
                using var client = new HttpClient(new Handler(async (_, token) => { cancel.Cancel(); await Task.Delay(Timeout.Infinite, token); return Binary(); }));
                try { await new OnlineSpeechSynthesizer(Credentials(SpeechProtocol.OpenAi), client).SynthesizeAsync("PUBLIC-TEXT", "en", cancel.Token); throw new Exception("Cancellation ignored."); }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
            });
            foreach (string json in new[] { "{}", "[]", "null", "{\"output\":{\"audio\":{\"url\":\"http://127.0.0.1/private\"}}}", "{\"output\":{\"audio\":{\"url\":\"https://unrelated.example.com/private\"}}}" })
                await Check("Qwen malformed/untrusted URL does not download", async () =>
                {
                    int calls = 0;
                    using var client = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) }); }));
                    await Reject(() => new OnlineSpeechSynthesizer(Credentials(SpeechProtocol.Qwen), client).SynthesizeAsync("PUBLIC-TEXT", "zh", CancellationToken.None));
                    Require(calls == 1, "Downloaded invalid audio location.");
                });
            foreach (string json in new[] { "{}", "{\"base_resp\":{\"status_code\":1004},\"data\":{\"audio\":\"deadbeef\"}}", "{\"base_resp\":{\"status_code\":0},\"data\":{\"audio\":\"zz\"}}" })
                await Check("MiniMax invalid response is controlled", async () =>
                {
                    using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) })));
                    await Reject(() => new OnlineSpeechSynthesizer(Credentials(SpeechProtocol.MiniMax), client).SynthesizeAsync("PUBLIC-TEXT", "en", CancellationToken.None));
                });
            await Check("network errors do not disclose text, key or endpoint", async () =>
            {
                using var client = new HttpClient(new Handler((_, _) => throw new HttpRequestException("PRIVATE-BODY PUBLIC-SECRET PUBLIC-TEXT speech.example.com")));
                await Reject(() => new OnlineSpeechSynthesizer(Credentials(SpeechProtocol.OpenAi), client).SynthesizeAsync("PUBLIC-TEXT", "en", CancellationToken.None));
            });
            await Check("invalid audio and oversized headers rejected", async () =>
            {
                foreach (bool oversized in new[] { false, true })
                {
                    using var client = new HttpClient(new Handler((_, _) =>
                    {
                        var result = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("PRIVATE-BODY") };
                        if (oversized) result.Content.Headers.ContentLength = 13 * 1024 * 1024;
                        return Task.FromResult(result);
                    }));
                    await Reject(() => new OnlineSpeechSynthesizer(Credentials(SpeechProtocol.OpenAi), client).SynthesizeAsync("PUBLIC-TEXT", "en", CancellationToken.None));
                }
            });
            await Check("playback mode, immutable provider snapshot, chunks, cancel and no hidden fallback", async () =>
            {
                var settings = new Settings(); var system = new SystemSpeech(); var player = new Player(); var synth = new Synth(); int factories = 0;
                var service = new ConfiguredTextSpeechService(settings, system, credentials => { factories++; Require(factories != 1 || credentials.Settings.Provider == "first", "Credentials changed after playback began."); return synth; }, player);
                Require(factories == 0 && settings.CredentialReads == 0 && system.Calls == 0, "Construction performed native/network work.");
                await service.SpeakAsync("hello", "en", CancellationToken.None);
                Require(system.Calls == 1 && factories == 0 && settings.CredentialReads == 0, "System mode loaded online credentials.");
                service.Mode = SpeechMode.Online;
                synth.OnCall = () => settings.Current = settings.Current with { Provider = "second" };
                await service.SpeakAsync(string.Join(" ", Enumerable.Repeat("Public speech sentence.", 100)), "en", CancellationToken.None);
                Require(factories == 1 && settings.CredentialReads == 1 && synth.Calls > 1 && player.Calls == synth.Calls, "Session recreated credentials or did not chunk/play.");
                synth.Fail = true; int before = system.Calls;
                try { await service.SpeakAsync("hello", "en", CancellationToken.None); throw new Exception("Failure ignored."); } catch (SpeechServiceException) { }
                Require(system.Calls == before, "Failure silently fell back to system speech.");
                synth.Fail = false; using var cancel = new CancellationTokenSource(); player.Cancel = cancel;
                try { await service.SpeakAsync(new string('a', 1300), "en", cancel.Token); throw new Exception("Playback cancellation ignored."); } catch (OperationCanceledException) { }
            });
            Console.WriteLine($"{passed} deterministic AI speech protocol/lifecycle checks passed; no external request or credential used."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static async Task Reject(Func<Task<SpeechAudio>> action)
    {
        try { await action(); throw new Exception("Invalid response accepted."); }
        catch (SpeechServiceException error) { Require(!error.Message.Contains("PRIVATE") && !error.Message.Contains("PUBLIC-") && !error.Message.Contains("example.com"), "Provider data leaked."); }
    }
    private static HttpResponseMessage Binary() => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Wave()) };
    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => callback(request, token); }
    private sealed class Settings : ISpeechSettingsStore
    {
        public SpeechMode Mode; public int CredentialReads;
        public OnlineSpeechSettings Current = new("first", SpeechProtocol.OpenAi, "https://speech.example.com/tts", "model", "voice");
        public SpeechMode ReadMode() => Mode; public void SaveMode(SpeechMode mode) => Mode = mode;
        public OnlineSpeechCredentials ReadCredentials(string? provider = null) { CredentialReads++; return new(Current, "PUBLIC-KEY"); }
        public OnlineSpeechSettings? Read(string? provider = null) => Current;
        public IReadOnlyList<OnlineSpeechSettings> ReadProfiles() => [Current];
        public void Save(OnlineSpeechSettings settings, string? key, SpeechMode mode, bool removeKey = false) { Current = settings; Mode = mode; }
        public void DeleteProfile(string provider) { }
    }
    private sealed class SystemSpeech : ITextSpeechService { public int Calls; public Task SpeakAsync(string text, string language, CancellationToken token) { Calls++; return Task.CompletedTask; } }
    private sealed class Synth : IOnlineSpeechSynthesizer
    {
        public int Calls; public bool Fail; public Action? OnCall;
        public Task<SpeechAudio> SynthesizeAsync(string text, string language, CancellationToken token)
        { Calls++; OnCall?.Invoke(); if (Fail) throw new SpeechServiceException("Controlled failure"); return Task.FromResult(new SpeechAudio(Wave(), "audio/wav")); }
    }
    private sealed class Player : ISpeechAudioPlayer
    {
        public int Calls; public CancellationTokenSource? Cancel;
        public Task PlayAsync(SpeechAudio audio, double speed, CancellationToken token) { Calls++; Cancel?.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    }
}
