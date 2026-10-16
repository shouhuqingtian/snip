using System;
using System.Threading;
using System.Threading.Tasks;
using Snip.Core;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace Snip.Infrastructure;

/// <summary>Bounded in-memory playback, created only after an explicit speech request.</summary>
public sealed class WindowsSpeechAudioPlayer : ISpeechAudioPlayer
{
    public async Task PlayAsync(SpeechAudio audio, double speed, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var stream = await CreateStreamAsync(audio, token).ConfigureAwait(false);
        using var source = MediaSource.CreateFromStream(stream, audio.ContentType);
        using var player = new MediaPlayer { AutoPlay = false, Source = source };
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnEnded(MediaPlayer sender, object args) => ended.TrySetResult();
        void OnOpened(MediaPlayer sender, object args) { try { sender.PlaybackSession.PlaybackRate = speed; } catch { } }
        void OnFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args) => ended.TrySetException(new SpeechServiceException("在线语音音频无法播放，请检查音频格式和输出设备。"));
        player.MediaEnded += OnEnded; player.MediaFailed += OnFailed; player.MediaOpened += OnOpened;
        try { player.Play(); await ended.Task.WaitAsync(TimeSpan.FromMinutes(3), token).ConfigureAwait(false); }
        finally
        {
            player.MediaEnded -= OnEnded; player.MediaFailed -= OnFailed; player.MediaOpened -= OnOpened;
            try { player.Pause(); player.Source = null; } catch { }
        }
    }
    public static async Task<InMemoryRandomAccessStream> CreateStreamAsync(SpeechAudio audio, CancellationToken token)
    {
        if (audio.Data.Length is 0 or > 12 * 1024 * 1024) throw new SpeechServiceException("语音音频为空或过大。");
        var stream = new InMemoryRandomAccessStream();
        try
        {
            using var writer = new DataWriter(stream);
            writer.WriteBytes(audio.Data);
            await writer.StoreAsync().AsTask(token).ConfigureAwait(false);
            await writer.FlushAsync().AsTask(token).ConfigureAwait(false);
            writer.DetachStream(); stream.Seek(0);
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

}
