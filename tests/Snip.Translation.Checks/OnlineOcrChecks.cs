using System.Net;
using System.Text;
using System.Text.Json;
using Snip.Core;
using Snip.Translation;

internal static class OnlineOcrChecks
{
    private static readonly ImagePayload Image = new(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a3foAAAAASUVORK5CYII="), 1, 1);
    public static async Task<int> RunAsync()
    {
        int passed = 0;
        async Task Check(string name, Func<Task> action) { await action(); passed++; Console.WriteLine("PASS OCR " + name); }
        try
        {
            foreach (bool separate in new[] { false, true })
                await Check("shared credentials with " + (separate ? "separate vision model" : "shared vision/translation model"), async () =>
                {
                    int calls = 0;
                    var settings = new OnlineTranslationSettings(OnlineTranslationProviders.Qwen, "https://public.example.com/v1", separate ? "qwen-plus" : "qwen-vl-plus", VisionModel: separate ? "qwen-vl-max" : "");
                    using var client = new HttpClient(new Handler(async (request, token) =>
                    {
                        calls++;
                        Require(request.RequestUri!.AbsolutePath == "/v1/chat/completions" && request.Headers.Authorization?.Parameter == "PUBLIC-KEY", "Shared endpoint/key lost.");
                        using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                        var body = json.RootElement;
                        Require(body.GetProperty("model").GetString() == (separate ? "qwen-vl-max" : "qwen-vl-plus"), "Wrong vision model.");
                        Require(!body.GetProperty("stream").GetBoolean() && !body.GetProperty("enable_thinking").GetBoolean(), "Unexpected streaming/thinking.");
                        var messages = body.GetProperty("messages");
                        Require(messages[0].GetProperty("content").GetString()!.Contains("Do not translate"), "OCR asks for translation.");
                        string url = messages[1].GetProperty("content")[1].GetProperty("image_url").GetProperty("url").GetString()!;
                        Require(url == "data:image/png;base64," + Convert.ToBase64String(Image.PngBytes.Span), "Screenshot data changed.");
                        return Reply("Google\n是否需要报关：");
                    }));
                    var result = await new OnlineOcrService(() => new(settings, "PUBLIC-KEY"), client).RecognizeAsync(Image, CancellationToken.None);
                    Require(calls == 1 && result.Regions.Single().Text == "Google\n是否需要报关：" && result.Regions[0].Confidence is null, "Text changed or fake confidence.");
                    Require(settings.Model == (separate ? "qwen-plus" : "qwen-vl-plus"), "Translation model was overwritten.");
                });
            await Check("text-only DeepSeek rejected before image is sent", async () =>
            {
                using var client = new HttpClient(new Handler((_, _) => throw new Exception("Image uploaded to text-only model.")));
                foreach (string name in new[] { "deepseek-chat", "deepseek-reasoner" })
                {
                    try { await new OnlineOcrService(() => new(OnlineTranslationProviders.Defaults(OnlineTranslationProviders.DeepSeek) with { Model = name }, "PUBLIC-KEY"), client).RecognizeAsync(Image, CancellationToken.None); throw new Exception("Text-only model accepted."); }
                    catch (InvalidOperationException e) { Require(e.Message.Contains("视觉模型"), "Missing setup guidance."); }
                }
            });
            foreach (var code in new[] { HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests, HttpStatusCode.ServiceUnavailable, HttpStatusCode.Redirect })
                await Check("HTTP " + (int)code + " preserves privacy and makes no retry/failover", async () =>
                {
                    int calls = 0;
                    using var client = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent("PRIVATE server echo KEY and image") }); }));
                    try { await Service(client).RecognizeAsync(Image, CancellationToken.None); throw new Exception("Failure accepted."); }
                    catch (InvalidOperationException e) { Require(calls == 1 && !e.ToString().Contains("PRIVATE"), "Error leakage or unexpected retry."); }
                });
            await Check("cancel interrupts request", async () =>
            {
                using var current = new CancellationTokenSource(50);
                using var client = new HttpClient(new Handler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Reply(""); }));
                try { await Service(client).RecognizeAsync(Image, current.Token); throw new Exception("Cancellation ignored."); }
                catch (OperationCanceledException) when (current.IsCancellationRequested) { }
            });
            await Check("oversize and invalid images rejected without upload", async () =>
            {
                using var client = new HttpClient(new Handler((_, _) => throw new Exception("Invalid image uploaded.")));
                foreach (var image in new[] { new ImagePayload(new byte[8 * 1024 * 1024 + 1], 1, 1), Image with { Width = 16000001 }, new ImagePayload(new byte[8], 1, 1) })
                {
                    try { await Service(client).RecognizeAsync(image, CancellationToken.None); throw new Exception("Invalid image accepted."); }
                    catch (InvalidOperationException) { }
                }
            });
            foreach (string data in new[] { "PRIVATE invalid JSON", "{}", "{\"choices\":[]}", "{\"choices\":[{\"message\":{\"content\":42}}]}", new string('x', 1_048_577) })
                await Check("malformed/oversized response rejected without provider content", async () =>
                {
                    using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(data) })));
                    try { await Service(client).RecognizeAsync(Image, CancellationToken.None); throw new Exception("Invalid response accepted."); }
                    catch (InvalidOperationException e) { Require(!e.ToString().Contains("PRIVATE"), "Response leaked."); }
                });
            await Check("empty OCR is empty, truncated/refused OCR is an error", async () =>
            {
                using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Reply(""))));
                Require((await Service(client).RecognizeAsync(Image, CancellationToken.None)).Regions.Count == 0, "Empty text fabricated.");
                foreach (string reason in new[] { "length", "content_filter" })
                {
                    using var truncated = new HttpClient(new Handler((_, _) => Task.FromResult(Reply("PARTIAL", reason))));
                    try { await Service(truncated).RecognizeAsync(Image, CancellationToken.None); throw new Exception("Partial OCR accepted as complete."); }
                    catch (InvalidOperationException e) { Require(!e.Message.Contains("PARTIAL"), "Partial output leaked."); }
                }
            });
            await Check("network exception does not reveal endpoint or payload", async () =>
            {
                using var client = new HttpClient(new Handler((_, _) => throw new HttpRequestException("PRIVATE endpoint")));
                try { await Service(client).RecognizeAsync(Image, CancellationToken.None); throw new Exception("Network failure ignored."); }
                catch (InvalidOperationException e) { Require(!e.ToString().Contains("PRIVATE"), "Network detail leaked."); }
            });
            Console.WriteLine(passed + " deterministic online OCR protocol checks passed; no external request or credential used.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static OnlineOcrService Service(HttpClient client) => new(() => new(new("custom", "https://public.example.com/v1", "public-vision-model"), "PUBLIC-KEY"), client);
    private static HttpResponseMessage Reply(string content, string finish = "stop") => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content }, finish_reason = finish } } }), Encoding.UTF8, "application/json") };
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
}
