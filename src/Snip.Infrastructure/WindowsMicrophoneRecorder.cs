using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Snip.Core;

namespace Snip.Infrastructure;

/// <summary>Default Windows microphone, 16 kHz mono PCM; no device or worker exists while idle.</summary>
public sealed class WindowsMicrophoneRecorder : IMicrophoneRecorder
{
    [StructLayout(LayoutKind.Sequential, Pack = 2)] private struct Format
    { public ushort Tag, Channels; public uint Samples, BytesPerSecond; public ushort Align, Bits, Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct Header
    { public IntPtr Data; public uint Length, Recorded; public UIntPtr User; public uint Flags, Loops; public IntPtr Next; public UIntPtr Reserved; }
    [DllImport("winmm.dll")] private static extern uint waveInGetNumDevs();
    [DllImport("winmm.dll")] private static extern uint waveInOpen(out IntPtr handle, uint device, ref Format format, IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] private static extern uint waveInPrepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveInUnprepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveInAddBuffer(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveInStart(IntPtr handle);
    [DllImport("winmm.dll")] private static extern uint waveInStop(IntPtr handle);
    [DllImport("winmm.dll")] private static extern uint waveInReset(IntPtr handle);
    [DllImport("winmm.dll")] private static extern uint waveInClose(IntPtr handle);
    public static bool HasDevice => waveInGetNumDevs() > 0;
    public Task<RecordedVoice> RecordAsync(CancellationToken stop, CancellationToken cancel, Action started) => Task.Run(() =>
    {
        cancel.ThrowIfCancellationRequested();
        if (!HasDevice) throw new VoiceInputException("未找到录音设备，请连接麦克风并设置 Windows 默认输入设备。", VoiceInputFailure.NoMicrophone);
        if (stop.IsCancellationRequested) throw new VoiceInputException("请按住语音快捷键说话，松开后再识别。");
        using var signal = new AutoResetEvent(false); using var pcm = new MemoryStream();
        var buffers = new List<(IntPtr Header, IntPtr Data, bool Prepared)>(); IntPtr device = IntPtr.Zero;
        var order = new RecordingBufferOrder(5);
        uint size = (uint)Marshal.SizeOf<Header>();
        var format = new Format { Tag = 1, Channels = 1, Samples = 16000, BytesPerSecond = 32000, Align = 2, Bits = 16 };
        void Check(uint code) { if (code != 0) throw new VoiceInputException("无法启用默认麦克风，请检查 Windows 麦克风权限、默认设备及设备占用。", VoiceInputFailure.DeviceAccess, (int)code); }
        void Drain(bool requeue)
        {
            order.Drain(index => (Marshal.PtrToStructure<Header>(buffers[index].Header).Flags & 1) != 0, index =>
            {
                var buffer = buffers[index];
                var header = Marshal.PtrToStructure<Header>(buffer.Header);
                int count = Math.Min(checked((int)header.Recorded), TranscriptionProviders.MaximumRecordingSeconds * 32000 - (int)pcm.Length);
                if (count > 0)
                {
                    byte[] bytes = new byte[count]; Marshal.Copy(buffer.Data, bytes, 0, count); pcm.Write(bytes, 0, count); CryptographicOperations.ZeroMemory(bytes);
                }
                if (requeue) Check(waveInAddBuffer(device, buffer.Header, size));
            });
        }
        try
        {
            Check(waveInOpen(out device, uint.MaxValue, ref format, signal.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, 0x50000));
            for (int i = 0; i < 5; i++)
            {
                IntPtr data = Marshal.AllocHGlobal(3200), header = Marshal.AllocHGlobal((int)size);
                buffers.Add((header, data, false));
                Marshal.StructureToPtr(new Header { Data = data, Length = 3200 }, header, false);
                Check(waveInPrepareHeader(device, header, size)); buffers[i] = (header, data, true);
                Check(waveInAddBuffer(device, header, size));
            }
            cancel.ThrowIfCancellationRequested(); Check(waveInStart(device)); started(); var clock = Stopwatch.StartNew();
            while (!stop.IsCancellationRequested && !cancel.IsCancellationRequested && clock.Elapsed.TotalSeconds < TranscriptionProviders.MaximumRecordingSeconds && pcm.Length < TranscriptionProviders.MaximumRecordingSeconds * 32000)
            { signal.WaitOne(30); Drain(true); }
            Check(waveInStop(device)); Check(waveInReset(device));
            if (!cancel.IsCancellationRequested) Drain(false);
            cancel.ThrowIfCancellationRequested();
            return new RecordedVoice(VoiceText.PcmToWav(pcm.GetBuffer().AsSpan(0, (int)pcm.Length)));
        }
        finally
        {
            if (device != IntPtr.Zero) { waveInStop(device); waveInReset(device); }
            foreach (var buffer in buffers)
            {
                bool released = !buffer.Prepared || device == IntPtr.Zero || waveInUnprepareHeader(device, buffer.Header, size) == 0;
                // Never free a buffer still owned by a faulty driver.
                if (released)
                {
                    Marshal.Copy(new byte[3200], 0, buffer.Data, 3200); Marshal.FreeHGlobal(buffer.Data); Marshal.FreeHGlobal(buffer.Header);
                }
            }
            if (device != IntPtr.Zero) waveInClose(device);
            if (pcm.TryGetBuffer(out var data) && data.Array is { } bytes) CryptographicOperations.ZeroMemory(bytes);
        }
    }, cancel);
}
