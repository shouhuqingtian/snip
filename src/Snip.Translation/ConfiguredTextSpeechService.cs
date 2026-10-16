using Snip.Core;

namespace Snip.Translation;

public sealed class ConfiguredTextSpeechService(ISpeechSettingsStore settings, ITextSpeechService system,
    Func<OnlineSpeechCredentials, IOnlineSpeechSynthesizer> factory, ISpeechAudioPlayer player) : IConfigurableTextSpeechService
{
    private readonly SemaphoreSlim playback = new(1, 1);
    public SpeechMode Mode { get => settings.ReadMode(); set => settings.SaveMode(value); }
    public Task SpeakAsync(string text, string language, CancellationToken token) => Task.Run(async () =>
    {
        // Pin both service and credentials once; changing settings never reroutes later chunks.
        SpeechMode mode; OnlineSpeechCredentials? credentials;
        try { mode = settings.ReadMode(); credentials = mode == SpeechMode.Online ? settings.ReadCredentials() : null; }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException)
        { throw new SpeechServiceException("在线语音配置不可用，请打开 AI 语音设置检查服务、地域和密钥，或手动切回系统语音。"); }
        var chunks = SpeechText.Chunks(text);
        await playback.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            if (credentials is null) { await system.SpeakAsync(text, language, token).ConfigureAwait(false); return; }
            var synthesizer = factory(credentials);
            foreach (var part in chunks)
            {
                token.ThrowIfCancellationRequested();
                var audio = await synthesizer.SynthesizeAsync(part, language, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                await player.PlayAsync(audio, credentials.Settings.Speed, token).ConfigureAwait(false);
            }
        }
        finally { playback.Release(); }
    }, token);
}
