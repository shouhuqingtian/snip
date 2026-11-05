using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using Snip.Core;

namespace Snip.Infrastructure;

public sealed record VoiceTargetInfo(bool Editable, bool Password, int ProcessId, int[] RuntimeId, int ControlTypeId = 0, bool KeyboardFocusable = false, bool? TextReadOnly = null, int? NativeFocusRole = null, int? NativeFocusState = null);
public interface IVoiceTargetProbe
{
    Task<VoiceTargetInfo?> ReadAsync(IntPtr foreground, CancellationToken token);
}
public interface IVoiceTargetProbeSession : IVoiceTargetProbe, IDisposable { }
public interface IReusableVoiceTargetProbe
{
    IVoiceTargetProbeSession OpenSession();
}
public sealed class VoiceTargetProbe(Func<ProcessStartInfo> host) : IVoiceTargetProbe, IReusableVoiceTargetProbe
{
    public event Action<int>? WorkerStarted;
    public IVoiceTargetProbeSession OpenSession() => new Session(host, id => WorkerStarted?.Invoke(id));
    public async Task<VoiceTargetInfo?> ReadAsync(IntPtr foreground, CancellationToken token)
    {
        using var session = OpenSession();
        return await session.ReadAsync(foreground, token).ConfigureAwait(false);
    }
    private sealed class Session(Func<ProcessStartInfo> host, Action<int> started) : IVoiceTargetProbeSession
    {
        private readonly SemaphoreSlim gate = new(1);
        private readonly CancellationTokenSource lifetime = new();
        private Process? process;
        private VoiceWorkerJob? job;
        private int disposed;
        public Task<VoiceTargetInfo?> ReadAsync(IntPtr foreground, CancellationToken token)
        {
            if (Volatile.Read(ref disposed) != 0) return Task.FromResult<VoiceTargetInfo?>(null);
            var lifetimeToken = lifetime.Token;
            return Task.Run(async () =>
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, lifetimeToken); deadline.CancelAfter(TimeSpan.FromSeconds(3));
                bool entered = false;
                try
                {
                    await gate.WaitAsync(deadline.Token).ConfigureAwait(false); entered = true;
                    deadline.Token.ThrowIfCancellationRequested();
                    if (process is null)
                    {
                        var info = host(); info.UseShellExecute = false; info.CreateNoWindow = true; info.RedirectStandardInput = info.RedirectStandardOutput = true;
                        info.ArgumentList.Add("--voice-target-worker"); process = Process.Start(info) ?? throw new InvalidOperationException();
                        job = VoiceWorkerJob.Attach(process);
                        started(process.Id);
                    }
                    await process.StandardInput.WriteLineAsync(foreground.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture)).ConfigureAwait(false);
                    string? line = await process.StandardOutput.ReadLineAsync(deadline.Token).ConfigureAwait(false);
                    if (line is null || line.Length > 8192) { StopCurrent(); return null; }
                    var result = JsonSerializer.Deserialize<VoiceTargetInfo>(line);
                    return result?.RuntimeId is { Length: >= 1 and <= 64 } ? result : null;
                }
                catch (Exception e) when (e is IOException or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
                { if (entered) StopCurrent(); token.ThrowIfCancellationRequested(); return null; }
                finally { if (entered) gate.Release(); }
            }, token);
        }
        private void StopCurrent() { job?.Dispose(); job = null; Stop(process); process = null; }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lifetime.Cancel();
            // Never wait for a native accessibility provider on the UI thread.
            _ = Task.Run(async () => { await gate.WaitAsync().ConfigureAwait(false); try { StopCurrent(); lifetime.Dispose(); } finally { gate.Release(); } });
        }
    }
    internal static void Stop(Process? process)
    {
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(true); process.WaitForExit(1000); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        finally { process.Dispose(); }
    }
}
public static class VoiceTargetWorker
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    public static int Run(TextReader input, TextWriter output)
    {
        using var identities = new NativeFocusIdentities();
        using var timeout = new Timer(_ => Environment.Exit(8), null, (TranscriptionProviders.MaximumRecordingSeconds + 110) * 1000, Timeout.Infinite);
        while (input.ReadLine() is { } line)
        {
            var result = Read(line, identities); output.WriteLine(JsonSerializer.Serialize(result)); output.Flush();
        }
        return 0;
    }
    private sealed class NativeFocusIdentities : IDisposable
    {
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        private readonly Dictionary<IntPtr, int> identities = new();
        private readonly List<object> references = new();
        public bool UseNative { get; private set; }
        public VoiceTargetInfo? Read(BrowserHoverHitTest.NativeFocus focus)
        {
            // Native MSAA ROLE_SYSTEM_TEXT, FOCUSED and FOCUSABLE. Keep object
            // identity alive only for this dictation, so a recycled pointer can
            // never identify a different field. No name/value/text is queried.
            if (focus.Accessible is null || focus.Role != 42 || focus.State is not { } state ||
                (state & 0x100004) != 0x100004 || (state & 0x18001) != 0) return null;
            IntPtr identity = Marshal.GetIUnknownForObject(focus.Accessible);
            if (identities.TryGetValue(identity, out int id)) Marshal.Release(identity);
            else
            {
                if (identities.Count >= 8) { Marshal.Release(identity); return null; }
                id = identities.Count + 1; identities.Add(identity, id); references.Add(focus.Accessible);
            }
            UseNative = true;
            GetWindowThreadProcessId(focus.Window, out uint process);
            return new((state & 0x40) == 0, (state & 0x20000000) != 0, (int)process,
                [-42, id, focus.Child], ControlType.Edit.Id, true, (state & 0x40) != 0, focus.Role, state);
        }
        public void Dispose()
        {
            foreach (var identity in identities.Keys) Marshal.Release(identity);
            identities.Clear(); references.Clear();
        }
    }
    private static VoiceTargetInfo Read(string line, NativeFocusIdentities identities)
    {
        VoiceTargetInfo result = new(false, false, 0, []);
        try
        {
            if (!long.TryParse(line, out long expected) || GetForegroundWindow().ToInt64() != expected) return result;
            var window = new IntPtr(expected);
            bool chromium = BrowserHoverHitTest.IsChromium(window);
            // Chromium enables its accessibility tree lazily for a new UIA client.
            // Initialize only this foreground window, then give the focused edit a
            // bounded chance to appear. Never accept the temporary browser pane as
            // the original input or enumerate page text.
            BrowserHoverHitTest.NativeFocus nativeFocus = new(window, null, 0, null, null);
            if (chromium) { nativeFocus = BrowserHoverHitTest.WarmFocusedAccessibility(window); _ = AutomationElement.FromHandle(nativeFocus.Window); }
            var warmup = Stopwatch.StartNew();
            do
            {
                if (GetForegroundWindow().ToInt64() != expected) break;
                if (chromium) nativeFocus = BrowserHoverHitTest.WarmFocusedAccessibility(window);
                if (nativeFocus.Role == 42 && nativeFocus.State is { } protectedState && (protectedState & 0x20000004) == 0x20000004)
                    return GetForegroundWindow().ToInt64() == expected
                        ? new(false, true, 0, [-1], NativeFocusRole: nativeFocus.Role, NativeFocusState: nativeFocus.State)
                        : result;
                var focused = AutomationElement.FocusedElement;
                if (chromium && (identities.UseNative || focused?.Current.ControlType == ControlType.Pane))
                {
                    var native = identities.Read(nativeFocus);
                    if (native is not null) result = native;
                    if (identities.UseNative)
                    {
                        if (result.Editable || result.Password || warmup.ElapsedMilliseconds >= 1000) break;
                        Thread.Sleep(50);
                        continue;
                    }
                }
                if (focused is not null)
                {
                    bool password = focused.Current.IsPassword, editable = false;
                    bool? textReadOnly = null;
                    if (focused.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)) editable = !((ValuePattern)pattern).Current.IsReadOnly;
                    else if (focused.TryGetCurrentPattern(TextPattern.Pattern, out var textPattern))
                    {
                        // Roleless contenteditable editors are exposed as documents, not Edit.
                        // Query only the read-only attribute, never the document's text.
                        textReadOnly = ((TextPattern)textPattern).DocumentRange.GetAttributeValue(TextPattern.IsReadOnlyAttribute) as bool?;
                        editable = textReadOnly == false;
                    }
                    else if (focused.Current.ControlType == ControlType.Edit) editable = true;
                    result = new(editable && focused.Current.IsEnabled && focused.Current.IsKeyboardFocusable, password, focused.Current.ProcessId, focused.GetRuntimeId(), focused.Current.ControlType.Id, focused.Current.IsKeyboardFocusable, textReadOnly, nativeFocus.Role, nativeFocus.State);
                }
                if (!chromium || result.Editable || result.Password || warmup.ElapsedMilliseconds >= 1000) break;
                Thread.Sleep(50); // Only this deadline-bound helper thread waits.
            } while (true);
            if (GetForegroundWindow().ToInt64() != expected) result = new(false, false, 0, []);
        }
        catch (Exception) { result = new(false, false, 0, []); }
        return result;
    }
}
/// <summary>Native microphone driver lives only in a bounded helper. Audio crosses an anonymous pipe, never disk.</summary>
public sealed class IsolatedMicrophoneRecorder(Func<ProcessStartInfo> host) : IMicrophoneRecorder
{
    public event Action<int>? WorkerStarted;
    public Task<RecordedVoice> RecordAsync(CancellationToken stop, CancellationToken cancel, Action started) => Task.Run(async () =>
    {
        Process? worker = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel); deadline.CancelAfter(TimeSpan.FromSeconds(TranscriptionProviders.MaximumRecordingSeconds + 10));
        try
        {
            var info = host(); info.UseShellExecute = false; info.CreateNoWindow = true; info.RedirectStandardInput = info.RedirectStandardOutput = true;
            info.ArgumentList.Add("--voice-record-worker"); worker = Process.Start(info) ?? throw new VoiceInputException("无法启动录音辅助进程。");
            using var job = VoiceWorkerJob.Attach(worker);
            WorkerStarted?.Invoke(worker.Id);
            var current = worker;
            using var released = stop.Register(() =>
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                try { current.StandardInput.WriteLine("stop"); current.StandardInput.Flush(); } catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException) { }
            });
            byte[] header = new byte[4];
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token); startup.CancelAfter(TimeSpan.FromSeconds(5));
            await current.StandardOutput.BaseStream.ReadExactlyAsync(header, startup.Token).ConfigureAwait(false);
            int size = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header);
            if (size == -1)
            {
                started(); await current.StandardOutput.BaseStream.ReadExactlyAsync(header, deadline.Token).ConfigureAwait(false);
                size = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header);
            }
            if (size <= -100 && size >= -100 - (int)VoiceInputFailure.Service)
            {
                await current.StandardOutput.BaseStream.ReadExactlyAsync(header, deadline.Token).ConfigureAwait(false);
                var reason = (VoiceInputFailure)(-100 - size); int nativeCode = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header);
                throw new VoiceInputException(reason switch
                {
                    VoiceInputFailure.NoMicrophone => "未找到默认麦克风，请连接麦克风并设置 Windows 输入设备。",
                    VoiceInputFailure.DeviceAccess => "无法使用默认麦克风，请检查 Windows 桌面应用麦克风权限、设备设置和占用。",
                    VoiceInputFailure.NoVoice => "没有录到清晰声音，请检查麦克风输入音量。",
                    VoiceInputFailure.TooShort => "录音太短，请说话后再结束录音。",
                    _ => "录音未完成，请检查默认麦克风并重试。"
                }, reason, nativeCode);
            }
            if (size < 44 || size > 44 + TranscriptionProviders.MaximumRecordingSeconds * 32000) throw new VoiceInputException("录音辅助进程返回了无效数据。");
            byte[] wav = new byte[size];
            try { await current.StandardOutput.BaseStream.ReadExactlyAsync(wav, deadline.Token).ConfigureAwait(false); return new RecordedVoice(wav); }
            catch { System.Security.Cryptography.CryptographicOperations.ZeroMemory(wav); throw; }
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { throw new VoiceInputException("录音辅助进程超时，麦克风已停止，请重试。", VoiceInputFailure.RecorderTimeout); }
        catch (IOException) { throw new VoiceInputException("录音辅助进程中断，麦克风已停止，请重试。"); }
        finally { VoiceTargetProbe.Stop(worker); }
    }, cancel);
}
public static class VoiceRecordingWorker
{
    public static async Task<int> RunAsync(TextReader input, Stream output, IMicrophoneRecorder? recorder = null)
    {
        using var stop = new CancellationTokenSource(); using var cancel = new CancellationTokenSource();
        using var watchdog = new Timer(_ => Environment.Exit(8), null, TimeSpan.FromSeconds(TranscriptionProviders.MaximumRecordingSeconds + 8), Timeout.InfiniteTimeSpan);
        _ = Task.Run(async () => { _ = await input.ReadLineAsync().ConfigureAwait(false); stop.Cancel(); });
        void Length(int length) { byte[] header = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header, length); output.Write(header); output.Flush(); }
        try
        {
            using var audio = await (recorder ?? new WindowsMicrophoneRecorder()).RecordAsync(stop.Token, cancel.Token, () => Length(-1)).ConfigureAwait(false);
            Length(audio.Wav.Length); await output.WriteAsync(audio.Wav).ConfigureAwait(false); await output.FlushAsync().ConfigureAwait(false); return 0;
        }
        catch (VoiceInputException error) { Length(-100 - (int)error.Failure); Length(error.NativeCode); return 1; }
        catch (Exception) { Length(-100); Length(0); return 1; }
    }
}
