using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Snip.Core;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;

namespace Snip.Infrastructure;

/// <summary>Windows installed voices only. No HTTP, temporary audio files, resident player or constructor-time native work.</summary>
public sealed class WindowsTextSpeechService : ITextSpeechService
{
    private readonly SemaphoreSlim playback = new(1, 1);
    public Task SpeakAsync(string text, string language, CancellationToken token) => Task.Run(async () =>
    {
        var chunks = SpeechText.Chunks(text);
        if (chunks.Count == 0) return;
        token.ThrowIfCancellationRequested();
        await playback.WaitAsync(token).ConfigureAwait(false);
        try { await PlayAsync(chunks, language, token).ConfigureAwait(false); }
        finally { playback.Release(); }
    }, token);

    private static async Task PlayAsync(System.Collections.Generic.IReadOnlyList<string> chunks, string language, CancellationToken token)
    {
        using var synthesizer = CreateSynthesizer(language);
        using var player = new MediaPlayer { AutoPlay = false };
        foreach (var part in chunks)
        {
            token.ThrowIfCancellationRequested();
            using var stream = await synthesizer.SynthesizeTextToStreamAsync(part).AsTask(token).WaitAsync(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            using var source = MediaSource.CreateFromStream(stream, stream.ContentType);
            var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnEnded(MediaPlayer sender, object args) => ended.TrySetResult();
            void OnFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args) => ended.TrySetException(new InvalidOperationException("Windows 音频播放失败，请检查音频输出设备。"));
            player.MediaEnded += OnEnded; player.MediaFailed += OnFailed;
            try
            {
                player.Source = source; player.Play();
                await ended.Task.WaitAsync(TimeSpan.FromMinutes(3), token).ConfigureAwait(false);
            }
            finally
            {
                player.MediaEnded -= OnEnded; player.MediaFailed -= OnFailed;
                try { player.Pause(); player.Source = null; } catch { }
            }
        }
    }

    /// <summary>Exercises actual synthesis independently of the presence of an audio output device.</summary>
    public Task<ulong> SynthesizeSampleAsync(string language, CancellationToken token) => Task.Run(async () =>
    {
        token.ThrowIfCancellationRequested();
        using var synthesizer = CreateSynthesizer(language);
        using var stream = await synthesizer.SynthesizeTextToStreamAsync(language == "zh" ? "你好，欢迎使用自用截图。" : "Hello world.").AsTask(token).WaitAsync(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return stream.Size;
    }, token);

    private static SpeechSynthesizer CreateSynthesizer(string language)
    {
        if (language is not ("en" or "zh")) throw new ArgumentOutOfRangeException(nameof(language));
        var voice = SpeechSynthesizer.AllVoices.Where(value => value.Language.StartsWith(language + "-", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(value => value.Language.Equals(language == "zh" ? "zh-CN" : "en-US", StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
        if (voice is null) throw new SpeechUnavailableException(language);
        var synthesizer = new SpeechSynthesizer();
        try { synthesizer.Voice = voice; return synthesizer; }
        catch { synthesizer.Dispose(); throw; }
    }
}
