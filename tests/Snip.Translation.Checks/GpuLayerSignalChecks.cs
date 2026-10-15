using Snip.Translation;

internal static class GpuLayerSignalChecks
{
    public static async Task RunAsync()
    {
        foreach (bool stdout in new[] { false, true })
        {
            var signal = new GpuLayerSignal();
            using var info = new ChunkedReader("00.001 I load_tensors: offloaded 33/33 layers to GPU\r\n");
            using var other = new StringReader("PUBLIC unrelated native output\n");
            await Task.WhenAll(signal.ReadAsync(stdout ? info : other), signal.ReadAsync(stdout ? other : info));
            Require(signal.Observed && signal.Layers == 33, "Startup confirmation lost through redirected stream or chunk boundary.");
        }
        var incomplete = new GpuLayerSignal();
        using (var reader = new StringReader("load_tensors: offloaded 17/33 layers to GPU")) await incomplete.ReadAsync(reader);
        Require(incomplete.Observed && incomplete.Layers == 17, "Final unterminated startup line ignored.");
        var zero = new GpuLayerSignal();
        using (var reader = new StringReader("load_tensors: offloaded 0/33 layers to GPU\n")) await zero.ReadAsync(reader);
        Require(zero.Observed && zero.Layers == 0, "Explicit CPU-only signal was mistaken for GPU success or absent output.");
        foreach (string text in new[]
        {
            "load_tensors: offloaded 34/33 layers to GPU\n", "load_tensors: offloaded 2/0 layers to GPU\n",
            "load_tensors: offloaded 999999999999/33 layers to GPU\n", "offloaded 33/33 layers to GPU\n",
            new string('x', 600) + " load_tensors: offloaded 33/33 layers to GPU\n"
        })
        {
            var invalid = new GpuLayerSignal();
            using var reader = new StringReader(text); await invalid.ReadAsync(reader);
            Require(!invalid.Observed && invalid.Layers == 0, "Invalid or oversized output was trusted as GPU confirmation.");
        }
        var bounded = new GpuLayerSignal();
        using (var reader = new ChunkedReader(new string('x', 1_000_000) + "\nload_tensors: offloaded 33/33 layers to GPU\n"))
            await bounded.ReadAsync(reader);
        Require(bounded.Observed && bounded.Layers == 33, "Oversized line prevented later startup confirmation.");
        Console.WriteLine("PASS bounded GPU startup signal: stdout/stderr, split reads, EOF, zero layers, invalid counts and oversized lines; only numeric confirmation retained.");
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class ChunkedReader(string text) : StringReader(text)
    {
        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(7, buffer.Length)], cancellationToken);
    }
}
