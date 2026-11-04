using System.Buffers.Binary;
using Snip.Core;
using Xunit;

namespace Snip.Core.Tests;

public sealed class VoiceInputTests
{
    [Fact]
    public void SilenceEndsShrinkUploadWhileContextAndInternalPausesArePreserved()
    {
        byte[] pcm = new byte[160000]; // Five seconds, including quiet ends and an internal pause.
        for (int i = 64000; i < 96000; i += 2)
            if (i < 76000 || i >= 84000) BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i), 1000);
        byte[] wav = VoiceText.PcmToWav(pcm);
        Assert.Equal(pcm.AsSpan(57600, 44800).ToArray(), wav[44..]);
        Assert.Equal(wav.Length - 44, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40)));
        Assert.True(wav.Length < pcm.Length / 3);
        // Speech right at the edges, including low-level leading speech, must remain intact.
        byte[] shortPcm = Enumerable.Repeat((byte)0, 16000).ToArray();
        for (int i = 0; i < shortPcm.Length; i += 2) BinaryPrimitives.WriteInt16LittleEndian(shortPcm.AsSpan(i), (short)(i < 6400 ? 10 : 1000));
        Assert.Equal(shortPcm, VoiceText.PcmToWav(shortPcm)[44..]);
    }
    [Fact]
    public void NativeAsrProtocolsPersistAndLegacyProfilesRemainCompatible()
    {
        var legacy = System.Text.Json.JsonSerializer.Deserialize<TranscriptionSettings>("{\"Provider\":\"PUBLIC\",\"Endpoint\":\"https://public.example.com/asr\",\"Model\":\"PUBLIC\"}")!;
        Assert.Equal(TranscriptionProtocol.OpenAi, legacy.Protocol);
        foreach (var name in new[] { "阿里云百炼", "智谱AI" })
        {
            var settings = TranscriptionProviders.Defaults(name);
            var restored = System.Text.Json.JsonSerializer.Deserialize<TranscriptionSettings>(System.Text.Json.JsonSerializer.Serialize(settings))!;
            Assert.Equal(settings, restored); Assert.NotEqual(TranscriptionProtocol.OpenAi, restored.Protocol);
        }
        Assert.Throws<ArgumentException>(() => TranscriptionProviders.Validate(legacy with { Protocol = (TranscriptionProtocol)42 }));
    }
    [Fact]
    public void ExistingVoicePreferencesKeepHoldModeAndBothModesSurviveRestart()
    {
        var legacy = System.Text.Json.JsonSerializer.Deserialize<VoiceInputPreferences>("{\"Enabled\":true,\"Hotkey\":{\"Modifiers\":3,\"VirtualKey\":32}}")!;
        legacy.Validate(); Assert.Equal(VoiceInputMode.Hold, legacy.Mode);
        foreach (var mode in Enum.GetValues<VoiceInputMode>())
        {
            var stored = legacy with { Mode = mode };
            var restored = System.Text.Json.JsonSerializer.Deserialize<VoiceInputPreferences>(System.Text.Json.JsonSerializer.Serialize(stored))!;
            restored.Validate(); Assert.Equal(stored, restored);
        }
        Assert.Throws<ArgumentException>(() => (legacy with { Mode = (VoiceInputMode)42 }).Validate());
    }
    [Fact]
    public void CoalescedDriverBuffersKeepPcmChronologicalAcrossWrapAndStop()
    {
        var order = new RecordingBufferOrder(5);
        bool[] ready = new bool[5]; byte[][] buffers = Enumerable.Range(0, 5).Select(_ => Array.Empty<byte>()).ToArray();
        var expected = new List<byte>(); var actual = new List<byte>();
        void Complete(int index, int sequence, int bytes = 2000)
        {
            byte[] pcm = new byte[bytes];
            for (int i = 0; i < bytes; i += 2) BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i), (short)((sequence + 1) * 1000));
            buffers[index] = pcm; ready[index] = true;
        }
        void Drain() => order.Drain(i => ready[i], i => { actual.AddRange(buffers[i]); ready[i] = false; buffers[i] = []; });
        for (int i = 0; i < 4; i++) { Complete(i, i); expected.AddRange(buffers[i]); }
        Drain();
        // A delayed callback crosses from the last driver slot into slot zero.
        Complete(4, 4); Complete(0, 5); expected.AddRange(buffers[4]); expected.AddRange(buffers[0]); Drain();
        Assert.Equal(expected, actual);
        // A later slot alone cannot overtake an unfinished older packet.
        Complete(2, 7); Drain(); Assert.Equal(expected, actual);
        Complete(1, 6); expected.AddRange(buffers[1]); expected.AddRange(buffers[2]); Drain();
        // Stop/reset returns a partial tail and empty queued buffers together.
        Complete(3, 8, 500); expected.AddRange(buffers[3]); Array.Fill(ready, true); Drain();
        Assert.Equal(expected.ToArray(), VoiceText.PcmToWav(actual.ToArray())[44..]);
    }
    [Fact]
    public void TypedRecognitionPreservesUnicodeButNeverContainsActionKeys()
    {
        Assert.Equal("中文 English 🙂  next line", VoiceText.ForTyping(" \u001b中文 English 🙂\r\nnext\tline\u0000\u0008 "));
        Assert.Throws<VoiceInputException>(() => VoiceText.ForTyping("\r\n\0\t"));
        Assert.Throws<VoiceInputException>(() => VoiceText.ForTyping(new string('x', 20001)));
    }
    [Fact]
    public void PcmBecomesBoundedMonoWaveAndSilenceNeverUploads()
    {
        byte[] pcm = new byte[16000]; for (int i = 0; i < pcm.Length; i += 2) BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i), (short)(i % 3000 - 1500));
        byte[] wav = VoiceText.PcmToWav(pcm);
        Assert.Equal("RIFF"u8.ToArray(), wav[..4]); Assert.Equal(16000, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)));
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22))); Assert.Equal(16, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(34)));
        Assert.Equal(pcm, wav[44..]); Assert.Throws<VoiceInputException>(() => VoiceText.PcmToWav(new byte[8000]));
        Assert.Throws<VoiceInputException>(() => VoiceText.PcmToWav(new byte[200]));
        Assert.Throws<VoiceInputException>(() => VoiceText.PcmToWav(new byte[TranscriptionProviders.MaximumRecordingSeconds * 32000 + 2]));
        using (var recorded = new RecordedVoice(wav)) Assert.DoesNotContain("RIFF", recorded.ToString());
        Assert.All(wav, b => Assert.Equal((byte)0, b));
    }
    [Fact]
    public void RecognitionHasSeparateEditableProvidersAndNoImplicitTextModel()
    {
        foreach (string name in TranscriptionProviders.Names.Where(n => n != "自定义（兼容接口）")) TranscriptionProviders.Validate(TranscriptionProviders.Defaults(name));
        Assert.Equal("https://api.groq.com/openai/v1/audio/transcriptions", TranscriptionProviders.Defaults("Groq").Endpoint);
        Assert.Equal(["whisper-large-v3-turbo", "whisper-large-v3"], TranscriptionProviders.Models("Groq"));
        Assert.Equal("whisper-v3", TranscriptionProviders.Defaults("Fireworks").Model);
        foreach (string name in TranscriptionProviders.Names.Where(n => n != "自定义（兼容接口）"))
            Assert.Contains(TranscriptionProviders.Defaults(name).Model, TranscriptionProviders.Models(name));
        Assert.Throws<ArgumentException>(() => TranscriptionProviders.Validate(new("public", "http://example.com/", "model")));
        Assert.Throws<ArgumentException>(() => TranscriptionProviders.Validate(new("public", "https://key:private@example.com/", "model")));
        Assert.Throws<ArgumentException>(() => TranscriptionProviders.Validate(new("public", "https://example.com/asr", "model", "PRIVATE")));
        VoiceInputPreferences.Default.Hotkey.Validate(); Assert.False(VoiceInputPreferences.Default.Enabled);
    }
}
