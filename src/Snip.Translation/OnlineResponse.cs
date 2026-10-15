using System.Text.Json;

namespace Snip.Translation;

/// <summary>Shared bounded transport for explicit OCR and translation requests.</summary>
internal static class OnlineResponse
{
    internal static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private const int Limit = 1_048_576;

    internal static async Task<JsonDocument> ReadAsync(HttpResponseMessage response, CancellationToken token, Func<InvalidOperationException> invalid)
    {
        if (response.Content.Headers.ContentLength > Limit) throw invalid();
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        for (int count; (count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0;)
        {
            if (output.Length + count > Limit) throw invalid();
            output.Write(buffer, 0, count);
        }
        token.ThrowIfCancellationRequested();
        // JsonDocument holds this buffer until disposal; avoid another full response-sized array.
        try { return JsonDocument.Parse(output.GetBuffer().AsMemory(0, checked((int)output.Length))); }
        catch (JsonException) { throw invalid(); }
    }
}
