namespace Snip.Translation;

internal sealed record CpuInferenceProfile(int Threads, int BatchThreads, int BatchSize, int MicroBatchSize, bool Repack)
{
    internal static CpuInferenceProfile Legacy { get; } = new(2, 2, 2048, 512, true);
    internal static CpuInferenceProfile ForProcessorCount(int count)
    {
        count = Math.Max(1, count);
        // Decode leaves about half the logical processors free; prompt processing
        // reserves one and never exceeds four. No extra repacked copy of weights.
        return new(Math.Clamp(count / 2, 1, 4), Math.Clamp(count - 1, 1, 4), 512, 128, false);
    }
}
