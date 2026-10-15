using System.Text;
using System.Text.RegularExpressions;

namespace Snip.Translation;

/// <summary>Retains only numeric startup confirmation, never native output or user text.</summary>
internal sealed class GpuLayerSignal
{
    // llama.cpp b11612 maps library INFO (including load_tensors) to common TRACE, level 4.
    internal const string LogVerbosity = "4";
    private static readonly Regex Signal = new(@"\bload_tensors:\s+offloaded ([0-9]+)/([0-9]+) layers to GPU\b", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private int layers, observed;
    internal int Layers => Volatile.Read(ref layers);
    internal bool Observed => Volatile.Read(ref observed) != 0;

    internal async Task ReadAsync(TextReader reader)
    {
        var line = new StringBuilder(512); var buffer = new char[1024]; bool overflow = false;
        void Consume()
        {
            if (overflow || line.Length == 0) return;
            var match = Signal.Match(line.ToString());
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out int count) ||
                !int.TryParse(match.Groups[2].Value, out int total) || total <= 0 || count > total) return;
            int previous;
            do { previous = Layers; if (count <= previous) break; }
            while (Interlocked.CompareExchange(ref layers, count, previous) != previous);
            Volatile.Write(ref observed, 1);
        }
        for (int count; (count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0;)
            for (int i = 0; i < count; i++)
            {
                char value = buffer[i];
                if (value == '\n') { Consume(); line.Clear(); overflow = false; }
                else if (line.Length < 512 && !overflow) line.Append(value);
                else overflow = true;
            }
        Consume(); // A final un-terminated line is still a valid startup confirmation.
    }
}
