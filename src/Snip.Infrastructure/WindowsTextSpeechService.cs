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
    public Task SpeakEnglishAsync(string text, CancellationToken token) => Task.Run(async () =>
    {
        var chunks = SpeechText.Chunks(text);
        if (chunks.Count == 0) return;
        token.ThrowIfCancellationRequested();
        using var synthesizer = CreateEnglishSynthesizer();
        using var player = new MediaPlayer { AutoPlay = false };
        foreach (var part in chunks)
        {
            token.ThrowIfCancellationRequested();
            using var stream = await synthesizer.SynthesizeTextToStreamAsync(part).AsTask(token).ConfigureAwait(false);
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
    }, token);

    /// <summary>Exercises actual synthesis independently of the presence of an audio output device.</summary>
    public Task<ulong> SynthesizeEnglishSampleAsync(CancellationToken token) => Task.Run(async () =>
    {
        token.ThrowIfCancellationRequested();
        using var synthesizer = CreateEnglishSynthesizer();
        using var stream = await synthesizer.SynthesizeTextToStreamAsync("Hello world.").AsTask(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return stream.Size;
    }, token);

    private static SpeechSynthesizer CreateEnglishSynthesizer()
    {
        var voice = SpeechSynthesizer.AllVoices.Where(value => value.Language.StartsWith("en-", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(value => value.Language.Equals("en-US", StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
        if (voice is null) throw new SpeechUnavailableException();
        var synthesizer = new SpeechSynthesizer();
        try { synthesizer.Voice = voice; return synthesizer; }
        catch { synthesizer.Dispose(); throw; }
    }
}
